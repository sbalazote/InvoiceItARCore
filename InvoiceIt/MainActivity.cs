using System;
using Android.App;
using Android.Support.V7.App;
using Android.Widget;
using Android.OS;
using Android.Opengl;
using Android.Util;
using Javax.Microedition.Khronos.Opengles;
using Android.Support.Design.Widget;
using System.Collections.Generic;
using Android.Views;
using Android.Support.V4.Content;
using Android.Support.V4.App;
using System.Collections.Concurrent;
using Uri = Android.Net.Uri;
using System.IO;
using System.Collections;
using System.Globalization;
using Android.Content;
using Google.AR.Core;
using Google.AR.Core.Exceptions;
using Android.Content.Res;
using Android.Graphics;
using Java.Nio;
using Camera = Google.AR.Core.Camera;
using Path = System.IO.Path;
using Environment = Android.OS.Environment;
using File = System.IO.File;
using IOException = Java.IO.IOException;

namespace InvoiceIt
{
    [Activity(Label = "InvoiceIt", MainLauncher = true, Icon = "@mipmap/bit_logo", Theme = "@style/Theme.AppCompat.NoActionBar", ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize, ScreenOrientation = Android.Content.PM.ScreenOrientation.Locked)]
    public class MainActivity : AppCompatActivity, GLSurfaceView.IRenderer, Android.Views.View.IOnTouchListener
    {
        private int _mWidth;
        private int _mHeight;
        private bool _capturePicture = false;
        const string TAG = "InvoiceIt";

        // Rendering. The Renderers are created here, and initialized when the GL surface is created.
        GLSurfaceView mSurfaceView;

        Session mSession;
        BackgroundRenderer mBackgroundRenderer = new BackgroundRenderer();
        GestureDetector mGestureDetector;
        Snackbar mLoadingMessageSnackbar = null;
        DisplayRotationHelper mDisplayRotationHelper;

        ObjectRenderer mVirtualObject = new ObjectRenderer();
        ObjectRenderer mVirtualObjectShadow = new ObjectRenderer();
        PlaneRenderer mPlaneRenderer = new PlaneRenderer();
        PointCloudRenderer mPointCloud = new PointCloudRenderer();

        // Temporary matrix allocated here to reduce number of allocations for each frame.
        static float[] mAnchorMatrix = new float[16];

        ConcurrentQueue<MotionEvent> mQueuedSingleTaps = new ConcurrentQueue<MotionEvent>();

        // Tap handling and UI.
        List<Anchor> mAnchors = new List<Anchor>();

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            SetContentView(Resource.Layout.Main);
            mSurfaceView = FindViewById<GLSurfaceView>(Resource.Id.surfaceview);
            Button button = FindViewById<Button>(Resource.Id.fboRecord_button);
            button.Click += delegate {
                //button.Text = string.Format("{0} clicks!", count++);
                this._capturePicture = true;
            };
            mDisplayRotationHelper = new DisplayRotationHelper(this);

            Java.Lang.Exception exception = null;
            string message = null;

            try
            {
                mSession = new Session(/*context=*/this);
            }
            catch (UnavailableArcoreNotInstalledException e)
            {
                message = "Please install ARCore";
                exception = e;
            }
            catch (UnavailableApkTooOldException e)
            {
                message = "Please update ARCore";
                exception = e;
            }
            catch (UnavailableSdkTooOldException e)
            {
                message = "Please update this app";
                exception = e;
            }
            catch (Java.Lang.Exception e)
            {
                exception = e;
                message = "This device does not support AR";
            }

            if (message != null)
            {
                Toast.MakeText(this, message, ToastLength.Long).Show();
                return;
            }

            // Create default config, check is supported, create session from that config.
            var config = new Google.AR.Core.Config(mSession);
            if (!mSession.IsSupported(config))
            {
                Toast.MakeText(this, "This device does not support AR", ToastLength.Long).Show();
                Finish();
                return;
            }

            AssetManager assets = this.Assets;
            var inputStream = assets.Open("receipts.imgdb");
            AugmentedImageDatabase imageDatabase = AugmentedImageDatabase.Deserialize(mSession, inputStream);

            config.AugmentedImageDatabase = imageDatabase;

            mSession.Configure(config);

            mGestureDetector = new Android.Views.GestureDetector(this, new SimpleTapGestureDetector
            {
                SingleTapUpHandler = (MotionEvent arg) => {
                    onSingleTap(arg);
                    return true;
                },
                DownHandler = (MotionEvent arg) => true
            });

            mSurfaceView.SetOnTouchListener(this);

            // Set up renderer.
            mSurfaceView.PreserveEGLContextOnPause = true;
            mSurfaceView.SetEGLContextClientVersion(2);
            mSurfaceView.SetEGLConfigChooser(8, 8, 8, 8, 16, 0); // Alpha used for plane blending.
            mSurfaceView.SetRenderer(this);
            mSurfaceView.RenderMode = Rendermode.Continuously;
        }


        protected override void OnResume()
        {
            base.OnResume();

            // ARCore requires camera permissions to operate. If we did not yet obtain runtime
            // permission on Android M and above, now is a good time to ask the user for it.
            if (ContextCompat.CheckSelfPermission(this, Android.Manifest.Permission.Camera) == Android.Content.PM.Permission.Granted)
            {
                if (mSession != null)
                {
                    showLoadingMessage();
                    // Note that order matters - see the note in onPause(), the reverse applies here.
                    mSession.Resume();
                }

                mSurfaceView.OnResume();
                mDisplayRotationHelper.OnResume();
            }
            else
            {
                ActivityCompat.RequestPermissions(this, new string[] { Android.Manifest.Permission.Camera }, 0);
            }
        }

        protected override void OnPause()
        {
            base.OnPause();
            if (ContextCompat.CheckSelfPermission(this, Android.Manifest.Permission.Camera) == Android.Content.PM.Permission.Granted)
            {
                // Note that the order matters - GLSurfaceView is paused first so that it does not try
                // to query the session. If Session is paused before GLSurfaceView, GLSurfaceView may
                // still call mSession.update() and get a SessionPausedException.
                mDisplayRotationHelper.OnPause();
                mSurfaceView.OnPause();
                if (mSession != null)
                    mSession.Pause();
            }
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

            if (ContextCompat.CheckSelfPermission(this, Android.Manifest.Permission.Camera) != Android.Content.PM.Permission.Granted)
            {
                Toast.MakeText(this, "Camera permission is needed to run this application", ToastLength.Long).Show();
                Finish();
            }

            Plugin.Permissions.PermissionsImplementation.Current.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        }

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);

            if (hasFocus)
            {
                // Standard Android full-screen functionality.
                //Window.DecorView.SystemUiVisibility = Android.Views.SystemUiFlags.LayoutStable
                //| Android.Views.SystemUiFlags.LayoutHideNavigation
                //| Android.Views.SystemUiFlags.LayoutFullscreen
                //| Android.Views.SystemUiFlags.HideNavigation
                //| Android.Views.SystemUiFlags.Fullscreen
                //| Android.Views.SystemUiFlags.ImmersiveSticky;

                Window.AddFlags(WindowManagerFlags.KeepScreenOn);
            }
        }

        private void onSingleTap(MotionEvent e)
        {
            // Queue tap if there is space. Tap is lost if queue is full.
            if (mQueuedSingleTaps.Count < 16)
                mQueuedSingleTaps.Enqueue(e);
        }


        public void OnSurfaceCreated(IGL10 gl, Javax.Microedition.Khronos.Egl.EGLConfig config)
        {
            GLES20.GlClearColor(0.1f, 0.1f, 0.1f, 1.0f);

            // Create the texture and pass it to ARCore session to be filled during update().
            mBackgroundRenderer.CreateOnGlThread(/*context=*/this);
            if (mSession != null)
                mSession.SetCameraTextureName(mBackgroundRenderer.TextureId);

            // Prepare the other rendering objects.
            try
            {
                mVirtualObject.CreateOnGlThread(/*context=*/this, "andy.obj", "andy.png");
                mVirtualObject.setMaterialProperties(0.0f, 3.5f, 1.0f, 6.0f);

                mVirtualObjectShadow.CreateOnGlThread(/*context=*/this,
                    "andy_shadow.obj", "andy_shadow.png");
                mVirtualObjectShadow.SetBlendMode(ObjectRenderer.BlendMode.Shadow);
                mVirtualObjectShadow.setMaterialProperties(1.0f, 0.0f, 0.0f, 1.0f);
            }
            catch (Java.IO.IOException e)
            {
                Log.Error(TAG, "Failed to read obj file");
            }

            try
            {
                mPlaneRenderer.CreateOnGlThread(/*context=*/this, "trigrid.png");
            }
            catch (Java.IO.IOException e)
            {
                Log.Error(TAG, "Failed to read plane texture");
            }
            mPointCloud.CreateOnGlThread(/*context=*/this);
        }

        public void OnSurfaceChanged(IGL10 gl, int width, int height)
        {
            mDisplayRotationHelper.OnSurfaceChanged(width, height);
            GLES20.GlViewport(0, 0, width, height);
            _mWidth = width;
            _mHeight = height;
        }

        public void DetectImages(Frame frame)
        {
            var updatedAugmentedImages = frame.GetUpdatedTrackables(Java.Lang.Class.FromType(typeof(AugmentedImage)));

            foreach (AugmentedImage img in updatedAugmentedImages)
            {
                // A tracked image has a match.
                if (img.TrackingState == TrackingState.Tracking)
                {
                    //  TODO save screenshot.
                }
            }
        }

        public void OnDrawFrame(IGL10 gl)
        {
            // Clear screen to notify driver it should not load any pixels from previous frame.
            GLES20.GlClear(GLES20.GlColorBufferBit | GLES20.GlDepthBufferBit);

            if (mSession == null)
                return;

            // Notify ARCore session that the view size changed so that the perspective matrix and the video background
            // can be properly adjusted
            mDisplayRotationHelper.UpdateSessionIfNeeded(mSession);

            try
            {
                // Obtain the current frame from ARSession. When the configuration is set to
                // UpdateMode.BLOCKING (it is by default), this will throttle the rendering to the
                // camera framerate.
                Frame frame = mSession.Update();
                Camera camera = frame.Camera;
                // Handle taps. Handling only one tap per frame, as taps are usually low frequency
                // compared to frame rate.
                MotionEvent tap = null;
                mQueuedSingleTaps.TryDequeue(out tap);

                DetectImages(frame);

                if (tap != null && camera.TrackingState == TrackingState.Tracking)
                {
                    foreach (var hit in frame.HitTest(tap))
                    {
                        var trackable = hit.Trackable;

                        // Check if any plane was hit, and if it was hit inside the plane polygon.
                        if (trackable is Plane && ((Plane)trackable).IsPoseInPolygon(hit.HitPose))
                        {
                            // Cap the number of objects created. This avoids overloading both the
                            // rendering system and ARCore.
                            if (mAnchors.Count >= 16)
                            {
                                mAnchors[0].Detach();
                                mAnchors.RemoveAt(0);
                            }
                            // Adding an Anchor tells ARCore that it should track this position in
                            // space.  This anchor is created on the Plane to place the 3d model
                            // in the correct position relative to both the world and to the plane
                            mAnchors.Add(hit.CreateAnchor());

                            // Hits are sorted by depth. Consider only closest hit on a plane.
                            break;
                        }
                    }
                }

                // Draw background.
                mBackgroundRenderer.Draw(frame);

                // If not tracking, don't draw 3d objects.
                if (camera.TrackingState == TrackingState.Paused)
                    return;

                // Get projection matrix.
                float[] projmtx = new float[16];
                camera.GetProjectionMatrix(projmtx, 0, 0.1f, 100.0f);

                // Get camera matrix and draw.
                float[] viewmtx = new float[16];
                camera.GetViewMatrix(viewmtx, 0);

                // Compute lighting from average intensity of the image.
                var lightIntensity = frame.LightEstimate.PixelIntensity;

                // Visualize tracked points.
                var pointCloud = frame.AcquirePointCloud();
                mPointCloud.Update(pointCloud);
                mPointCloud.Draw(camera.DisplayOrientedPose, viewmtx, projmtx);

                // App is repsonsible for releasing point cloud resources after using it
                pointCloud.Release();

                var planes = new List<Plane>();
                foreach (var p in mSession.GetAllTrackables(Java.Lang.Class.FromType(typeof(Plane))))
                {
                    var plane = (Plane)p;
                    planes.Add(plane);
                }

                // Check if we detected at least one plane. If so, hide the loading message.
                if (mLoadingMessageSnackbar != null)
                {
                    foreach (var plane in planes)
                    {
                        if (plane.GetType() == Plane.Type.HorizontalUpwardFacing
                                && plane.TrackingState == TrackingState.Tracking)
                        {
                            hideLoadingMessage();
                            break;
                        }
                    }
                }

                // Visualize planes.
                mPlaneRenderer.DrawPlanes(planes, camera.DisplayOrientedPose, projmtx);

                // Visualize anchors created by touch.
                float scaleFactor = 1.0f;
                foreach (var anchor in mAnchors)
                {
                    if (anchor.TrackingState != TrackingState.Tracking)
                        continue;

                    // Get the current combined pose of an Anchor and Plane in world space. The Anchor
                    // and Plane poses are updated during calls to session.update() as ARCore refines
                    // its estimate of the world.
                    anchor.Pose.ToMatrix(mAnchorMatrix, 0);

                    // Update and draw the model and its shadow.
                    mVirtualObject.updateModelMatrix(mAnchorMatrix, scaleFactor);
                    mVirtualObjectShadow.updateModelMatrix(mAnchorMatrix, scaleFactor);
                    mVirtualObject.Draw(viewmtx, projmtx, lightIntensity);
                    mVirtualObjectShadow.Draw(viewmtx, projmtx, lightIntensity);
                }

            }
            catch (System.Exception ex)
            {
                // Avoid crashing the application due to unhandled exceptions.
                Log.Error(TAG, "Exception on the OpenGL thread", ex);
            }

            if (_capturePicture)
            {
                _capturePicture = false;
                SavePicture(gl);
            }
        }

        public static Uri GetOutputMediaFile(Context context, string subdir, string name, bool isPhoto, bool saveToAlbum)
        {
            subdir = subdir ?? string.Empty;

            if (string.IsNullOrWhiteSpace(name))
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                if (isPhoto)
                    name = "IMG_" + timestamp + ".jpg";
                else
                    name = "VID_" + timestamp + ".mp4";
            }

            var mediaType = (isPhoto) ? Environment.DirectoryPictures : Environment.DirectoryMovies;
            var directory = saveToAlbum ? Environment.GetExternalStoragePublicDirectory(mediaType) : context.GetExternalFilesDir(mediaType);
            using (var mediaStorageDir = new Java.IO.File(directory, subdir))
            {
                if (!mediaStorageDir.Exists())
                {
                    if (!mediaStorageDir.Mkdirs())
                        throw new IOException("Couldn't create directory, have you added the WRITE_EXTERNAL_STORAGE permission?");

                    if (!saveToAlbum)
                    {
                        // Ensure this media doesn't show up in gallery apps
                        using (var nomedia = new Java.IO.File(mediaStorageDir, ".nomedia"))
                            nomedia.CreateNewFile();
                    }
                }

                return Uri.FromFile(new Java.IO.File(GetUniquePath(mediaStorageDir.Path, name, isPhoto)));
            }
        }

        private static string GetUniquePath(string folder, string name, bool isPhoto)
        {
            var ext = Path.GetExtension(name);
            if (ext == string.Empty)
                ext = ((isPhoto) ? ".jpg" : ".mp4");

            name = Path.GetFileNameWithoutExtension(name);

            var nname = name + ext;
            var i = 1;
            while (File.Exists(Path.Combine(folder, nname)))
                nname = name + "_" + (i++) + ext;

            return Path.Combine(folder, nname);
        }

        /*public void OnSavePicture(View view)
        {
            // Here just a set a flag so we can copy
            // the image from the onDrawFrame() method.
            // This is required for OpenGL so we are on the rendering thread.
            this._capturePicture = true;
        }
*/

        /**
        * Call from the GLThread to save a picture of the current frame.
        */
        public void SavePicture(IGL10 mGL)
        {
            var pixelData = new int[_mWidth * _mHeight];

            // Read the pixels from the current GL frame.
            IntBuffer buf = IntBuffer.Wrap(pixelData);
            IntBuffer ibt = IntBuffer.Allocate(_mWidth * _mHeight);

            buf.Position(0);
            mGL.GlReadPixels(0, 0, _mWidth, _mHeight, GLES20.GlRgba, GLES20.GlUnsignedByte, buf);

            // Create a file in the Pictures/HelloAR album.
            var file = new Java.IO.File($"{Environment.GetExternalStoragePublicDirectory(Environment.DirectoryPictures)}/HelloAR", "Img" + DateTime.Now + ".png");

            // Make sure the directory exists
            if (!file.ParentFile.Exists())
            {
                file.ParentFile.Mkdirs();
            }

            // Convert the pixel data from RGBA to what Android wants, ARGB.
            var bitmapData = new int[pixelData.Length];
            for (int i = 0; i < _mHeight; i++)
            {
                for (int j = 0; j < _mWidth; j++)
                {
                    long p = pixelData[i * _mWidth + j];
                    long b = (p & 0x00ff0000) >> 16;
                    long r = (p & 0x000000ff) << 16;
                    long ga = p & 0xff00ff00;
                    bitmapData[(_mHeight - i - 1) * _mWidth + j] = (int)(ga | r | b);
                }
            }

            // Convert upside down mirror-reversed image to right-side up normal
            // image.
            for (int i = 0; i < _mHeight; i++)
            {
                for (int j = 0; j < _mWidth; j++)
                {
                    ibt.Put((_mHeight - i - 1) * _mWidth + j, buf.Get(i * _mWidth + j));
                }
            }

            // Create a bitmap.
            Bitmap bmp = Bitmap.CreateBitmap(bitmapData, _mWidth, _mHeight, Bitmap.Config.Argb8888);

            //bmp.EraseColor(Color.Argb(0, 255, 255, 255));
            bmp.CopyPixelsFromBuffer(ibt);

            var fs = new FileStream(file.Path, FileMode.OpenOrCreate);
            // Write it to disk.
            //FileOutputStream fos = new FileOutputStream(file);
            bmp.Compress(Bitmap.CompressFormat.Png, 100, fs);
            fs.Flush();
            fs.Close();

        }

        private void showLoadingMessage()
        {
            this.RunOnUiThread(() =>
            {
                mLoadingMessageSnackbar = Snackbar.Make(FindViewById(Android.Resource.Id.Content),
        "Searching for surfaces...", Snackbar.LengthIndefinite);
                mLoadingMessageSnackbar.View.SetBackgroundColor(Android.Graphics.Color.DarkGray);
                mLoadingMessageSnackbar.Show();
            });
        }

        private void hideLoadingMessage()
        {
            this.RunOnUiThread(() =>
            {
                mLoadingMessageSnackbar.Dismiss();
                mLoadingMessageSnackbar = null;
            });

        }

        public bool OnTouch(View v, MotionEvent e)
        {
            return mGestureDetector.OnTouchEvent(e);
        }
    }

    class SimpleTapGestureDetector : GestureDetector.SimpleOnGestureListener
    {
        public Func<MotionEvent, bool> SingleTapUpHandler { get; set; }

        public override bool OnSingleTapUp(MotionEvent e)
        {
            return SingleTapUpHandler?.Invoke(e) ?? false;
        }

        public Func<MotionEvent, bool> DownHandler { get; set; }

        public override bool OnDown(MotionEvent e)
        {
            return DownHandler?.Invoke(e) ?? false;
        }
    }
}