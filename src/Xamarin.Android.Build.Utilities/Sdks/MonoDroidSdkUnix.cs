﻿using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace Xamarin.Android.Build.Utilities
{
	class MonoDroidSdkUnix : MonoDroidSdkBase
	{
		readonly static string[] RuntimeToFrameworkPaths = new[]{
			Path.Combine ("..", "..", "..", ".xamarin.android", "lib", "xbuild-frameworks", "MonoAndroid"),
			Path.Combine ("..", "xbuild-frameworks", "MonoAndroid"),
			Path.Combine ("..", "mono", "2.1"),
		};

		readonly static string[] SearchPaths = {
			"/Library/Frameworks/Xamarin.Android.framework/Versions/Current/lib/mandroid",
			"/Developer/MonoAndroid/usr/lib/mandroid",
			"/opt/mono-android/lib/mandroid"
		};

		protected override string FindRuntime ()
		{
			string monoAndroidPath = Environment.GetEnvironmentVariable ("MONO_ANDROID_PATH");
			if (!string.IsNullOrEmpty (monoAndroidPath)) {
				string libMandroid = Path.Combine (monoAndroidPath, "lib", "mandroid");
				if (Directory.Exists (libMandroid) && ValidateRuntime (libMandroid))
					return libMandroid;
			}

			// check also in the users folder
			var personal = Environment.GetFolderPath (Environment.SpecialFolder.Personal);
			var additionalSearchPaths = new [] { Path.Combine (personal, @".xamarin.android/lib/mandroid") };

			return additionalSearchPaths.Concat (SearchPaths).FirstOrDefault (ValidateRuntime);
		}

		protected override bool ValidateBin (string binPath)
		{
			return !string.IsNullOrWhiteSpace (binPath) &&
				File.Exists (Path.Combine (binPath, "generator"));
		}

		protected override string FindFramework (string runtimePath)
		{
			foreach (var relativePath in RuntimeToFrameworkPaths) {
				var fullPath = Path.GetFullPath (Path.Combine (runtimePath, relativePath));
				if (Directory.Exists (fullPath)) {
					if (ValidateFramework (fullPath))
						return fullPath;

					// check to see if full path is the folder that contains each framework version, eg contains folders of the form v1.0, v2.3 etc
					var subdirs = Directory.GetDirectories (fullPath, "v*").OrderBy (x => x).ToArray ();
					foreach (var subdir in subdirs) {
						if (ValidateFramework (subdir))
							return subdir;
					}
				}
			}

			return null;
		}

		protected override string FindBin (string runtimePath)
		{
			string binPath = Path.GetFullPath (Path.Combine (runtimePath, "..", "..", "bin"));
			Console.WriteLine (binPath);
			if (File.Exists (Path.Combine (binPath, "generator")))
				return binPath;
			return null;
		}

		protected override string FindInclude (string runtimePath)
		{
			string includeDir = Path.GetFullPath (Path.Combine (runtimePath, "..", "..", "include"));
			if (Directory.Exists (includeDir))
				return includeDir;
			return null;
		}

		protected override string FindLibraries (string runtimePath)
		{
			return Path.GetFullPath (Path.Combine (runtimePath, ".."));
		}

		protected override IEnumerable<string> GetVersionFileLocations ()
		{
			yield return Path.GetFullPath (Path.Combine (RuntimePath, "..", "..", "Version"));
			string sdkPath = Path.GetDirectoryName (Path.GetDirectoryName (RuntimePath));
			if (Path.GetFileName (sdkPath) == "usr")
				yield return Path.GetFullPath (Path.Combine (Path.GetDirectoryName (sdkPath), "Version"));
		}
	}
}

