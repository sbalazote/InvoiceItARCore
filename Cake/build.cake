#load "common.cake"

var TARGET = Argument ("t", Argument ("target", "Default"));
var NUGET_VERSION = "1.3.0";
var AAR_URL = "https://dl.google.com/dl/android/maven2/com/google/ar/core/1.4.0/core-1.4.0.aar";
var SCENEFORM_URL = "https://maven.google.com/com/google/ar/sceneform/core/1.4.0/core-1.4.0.aar";
var RENDERING_URL = "https://maven.google.com/com/google/ar/sceneform/rendering/1.4.0/rendering-1.4.0.aar";
var BASE_URL = "https://maven.google.com/com/google/ar/sceneform/sceneform-base/1.4.0/sceneform-base-1.4.0.aar";
var FILAMENT_URL = "https://maven.google.com/com/google/ar/sceneform/filament-android/1.4.0/filament-android-1.4.0.aar";
var PLUGIN_URL = "https://maven.google.com/com/google/ar/sceneform/plugin/1.4.0/plugin-1.4.0.jar";
var UX_URL = "https://maven.google.com/com/google/ar/sceneform/ux/sceneform-ux/1.4.0/sceneform-ux-1.4.0.aar";
var OBJ_URL = "https://oss.sonatype.org/content/repositories/releases/de/javagl/obj/0.3.0/obj-0.3.0.jar";

var buildSpec = new BuildSpec () {
	Libs = new [] {
		new DefaultSolutionBuilder {
			SolutionPath = "./Google.ARSceneform.sln",
			OutputFiles = new [] { 
				new OutputFileCopy {
					FromFile = "./Google.ARSceneform/bin/Release/Xamarin.Google.ARCore.dll",
				}
			}
		}
	},

	NuGets = new [] {
		new NuGetInfo { NuSpec = "./nuget/Xamarin.Google.ARCore.Sceneform.nuspec", Version = NUGET_VERSION },
	},
};

Task ("externals")
	.Does (() => 
{
	var AAR_FILE = "../externals/core.aar";
	var OBJ_JAR_FILE = "../externals/obj.jar";
	var SCENEFORM_FILE = "../externals/sceneform.aar";
	var RENDERING_FILE = "../externals/rendering.aar";
	var BASE_FILE = "../externals/sceneform-base.aar";
	var FILAMENT_FILE = "../externals/filament-android.aar";
	var PLUGIN_FILE = "../externals/plugin.jar";
	var UX_FILE = "../externals/sceneform-ux.aar";

	if (!DirectoryExists ("../externals/"))
		CreateDirectory ("../externals");

	if (!FileExists (AAR_FILE))
		DownloadFile (AAR_URL, AAR_FILE);
		
	if (!FileExists (OBJ_JAR_FILE))
		DownloadFile (OBJ_URL, OBJ_JAR_FILE);
		
	if (!FileExists (SCENEFORM_FILE))
		DownloadFile (SCENEFORM_URL, SCENEFORM_FILE);
		
	if (!FileExists (RENDERING_FILE))
		DownloadFile (RENDERING_URL, RENDERING_FILE);
		
	if (!FileExists (BASE_FILE))
		DownloadFile (BASE_URL, BASE_FILE);
		
	if (!FileExists (FILAMENT_FILE))
		DownloadFile (FILAMENT_URL, FILAMENT_FILE);
		
	if (!FileExists (PLUGIN_FILE))
		DownloadFile (PLUGIN_URL, PLUGIN_FILE);

	if (!FileExists (UX_FILE))
		DownloadFile (UX_URL, UX_FILE);
});


Task ("clean").IsDependentOn ("clean-base").Does (() => 
{	
	if (DirectoryExists ("../externals"))
		DeleteDirectory ("../externals", true);
});

SetupXamarinBuildTasks (buildSpec, Tasks, Task);

RunTarget (TARGET);