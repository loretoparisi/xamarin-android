using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.IO;
using System.Security.Cryptography;
using Ionic.Zip;
using Mono.Security.Cryptography;
using Xamarin.Android.Build.Utilities;


#if MSBUILD
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
#endif

using Xamarin.Android.Tools;

namespace Xamarin.Android.Tasks
{
	public class MonoAndroidHelper
	{
		// Set in ResolveSdks.Execute();
		// Requires that ResolveSdks.Execute() run before anything else
		public static string[] TargetFrameworkDirectories;
		readonly static byte[] Utf8Preamble = System.Text.Encoding.UTF8.GetPreamble ();

		public static int RunProcess (string name, string args, DataReceivedEventHandler onOutput, DataReceivedEventHandler onError)
		{
			var psi = new ProcessStartInfo (name, args) {
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			Process p = new Process ();
			p.StartInfo = psi;
			
			p.OutputDataReceived += onOutput;
			p.ErrorDataReceived += onError;
			p.Start ();
			p.BeginErrorReadLine ();
			p.BeginOutputReadLine ();
			p.WaitForExit ();
			try {
				return p.ExitCode;
			} finally {
				p.Close ();
			}
		}

#if MSBUILD
		public static void RefreshAndroidSdk (string sdkPath, string ndkPath, string javaPath)
		{
			AndroidSdk.Refresh (sdkPath, ndkPath, javaPath);
		}

		public static void RefreshMonoDroidSdk (string toolsPath, string binPath, string[] referenceAssemblyPaths)
		{
			MonoDroidSdk.Refresh (toolsPath, binPath,
					(from   refPath in referenceAssemblyPaths
					 where  !string.IsNullOrEmpty (refPath)
					 let    path = refPath.TrimEnd (Path.DirectorySeparatorChar)
					 where  File.Exists (Path.Combine (path, "mscorlib.dll"))
					 select path)
					.FirstOrDefault ());
		}
#endif  // MSBUILD

		class SizeAndContentFileComparer : IEqualityComparer<FileInfo>
#if MSBUILD
			, IEqualityComparer<ITaskItem>
#endif  // MSBUILD
		{
			public  static  readonly  SizeAndContentFileComparer  DefaultComparer     = new SizeAndContentFileComparer ();

			public bool Equals (FileInfo x, FileInfo y)
			{
				if (x.Exists != y.Exists || x.Length != y.Length)
					return false;
				using (var f1 = File.OpenRead (x.FullName)) {
					using (var f2 = File.OpenRead (y.FullName)) {
						var b1 = new byte [0x1000];
						var b2 = new byte [0x1000];
						int total = 0;
						while (total < x.Length) {
							int size = f1.Read (b1, 0, b1.Length);
							total += size;
							f2.Read (b2, 0, b2.Length);
							if (!b1.Take (size).SequenceEqual (b2.Take (size)))
								return false;
						}
					}
				}
				return true;
			}

			public int GetHashCode (FileInfo obj)
			{
				return (int) obj.Length;
			}

#if MSBUILD
			public bool Equals (ITaskItem x, ITaskItem y)
			{
				return Equals (new FileInfo (x.ItemSpec), new FileInfo (y.ItemSpec));
			}

			public int GetHashCode (ITaskItem obj)
			{
				return GetHashCode (new FileInfo (obj.ItemSpec));
			}
#endif  // MSBUILD
		}

		internal static bool LogInternalExceptions {
			get {
				return string.Equals (
						"icanhaz",
						Environment.GetEnvironmentVariable ("__XA_LOG_ERRORS__"),
						StringComparison.OrdinalIgnoreCase);
			}
		}

#if MSBUILD
		public static IEnumerable<string> ExpandFiles (ITaskItem[] libraryProjectJars)
		{
			libraryProjectJars  = libraryProjectJars ?? new ITaskItem [0];
			return (from path in libraryProjectJars
					let     dir     = Path.GetDirectoryName (path.ItemSpec)
					let     pattern = Path.GetFileName (path.ItemSpec)
					where   Directory.Exists (dir)
					select  Directory.GetFiles (dir, pattern))
				.SelectMany (paths => paths);
		}

		public static IEnumerable<ITaskItem> DistinctFilesByContent (IEnumerable<ITaskItem> filePaths)
		{
			return filePaths.Distinct (MonoAndroidHelper.SizeAndContentFileComparer.DefaultComparer);
		}
#endif

		public static IEnumerable<string> DistinctFilesByContent (IEnumerable<string> filePaths)
		{
			return filePaths.Select (p => new FileInfo (p)).ToArray ().Distinct (new MonoAndroidHelper.SizeAndContentFileComparer ()).Select (f => f.FullName).ToArray ();
		}
		
		public static IEnumerable<string> GetDuplicateFileNames (IEnumerable<string> fullPaths, string [] excluded)
		{
			var files = fullPaths.Select (full => Path.GetFileName (full)).Where (f => excluded == null || !excluded.Contains (f, StringComparer.OrdinalIgnoreCase)).ToArray ();
			for (int i = 0; i < files.Length; i++)
				for (int j = i + 1; j < files.Length; j++)
					if (String.Compare (files [i], files [j], StringComparison.OrdinalIgnoreCase) == 0)
						yield return files [i];
		}
		
		public static bool IsEmbeddedReferenceJar (string jar)
		{
			return jar.StartsWith ("__reference__");
		}

		public static void LogWarning (object log, string msg, params object [] args)
		{
#if MSBUILD
			var helper = log as TaskLoggingHelper;
			if (helper != null) {
				helper.LogWarning (msg, args);
				return;
			}
			var action = log as Action<string>;
			if (action != null) {
				action (string.Format (msg, args));
				return;
			}
#else
			Console.Error.WriteLine (msg, args);
#endif
		}

#if MSBUILD

		static readonly string[] ValidAbis = new[]{
			"arm64-v8a",
			"armeabi",
			"armeabi-v7a",
			"x86",
			"x86_64",
		};

		public static string GetNativeLibraryAbi (string lib)
		{
			var dirs = lib.ToLowerInvariant ().Split ('/', '\\');

			return ValidAbis.Where (p => dirs.Contains (p)).FirstOrDefault ();
		}

		public static string GetNativeLibraryAbi (ITaskItem lib)
		{
			// If Abi is explicitly specified, simply return it.
			var lib_abi = lib.GetMetadata ("Abi");

			if (!string.IsNullOrWhiteSpace (lib_abi))
				return lib_abi;

			// Try to figure out what type of abi this is from the path
			// First, try nominal "Link" path.
			var link = lib.GetMetadata ("Link");
			if (!string.IsNullOrWhiteSpace (link)) {
				var linkdirs = link.ToLowerInvariant ().Split ('/', '\\');
				lib_abi = ValidAbis.Where (p => linkdirs.Contains (p)).FirstOrDefault ();
			}
			
			if (!string.IsNullOrWhiteSpace (lib_abi))
				return lib_abi;

			// If not resolved, use ItemSpec
			return GetNativeLibraryAbi (lib.ItemSpec);
		}
#endif

		public static bool IsFrameworkAssembly (string assembly)
		{
			return IsFrameworkAssembly (assembly, false);
		}

		public static bool IsFrameworkAssembly (string assembly, bool checkSdkPath)
		{
			var assemblyName = Path.GetFileName (assembly);

			if (Profile.SharedRuntimeAssemblies.Contains (assemblyName, StringComparer.InvariantCultureIgnoreCase)) {
#if MSBUILD
				bool treatAsUser = Array.BinarySearch (FrameworkAssembliesToTreatAsUserAssemblies, assemblyName, StringComparer.OrdinalIgnoreCase) >= 0;
				// Framework assemblies don't come from outside the SDK Path;
				// user assemblies do
				if (checkSdkPath && treatAsUser && TargetFrameworkDirectories != null) {
					return TargetFrameworkDirectories
						// TargetFrameworkDirectories will contain a "versioned" directory,
						// e.g. $prefix/lib/xbuild-frameworks/MonoAndroid/v1.0.
						// Trim off the version.
						.Select (p => Path.GetDirectoryName (p.TrimEnd (Path.DirectorySeparatorChar)))
						.Any (p => assembly.StartsWith (p));
				}
#endif
				return true;
			}

			return false;
		}

		public static bool IsForceRetainedAssembly (string assembly)
		{
			switch (assembly) {
			case "Mono.Android.Export.dll": // this is totally referenced by reflection.
				return true;
			}
			return false;
		}

#if MSBUILD
		public static void SetLastAccessAndWriteTimeUtc (string source, DateTime dateUtc, TaskLoggingHelper Log)
		{
			try {
				File.SetLastWriteTimeUtc (source, dateUtc);
				File.SetLastAccessTimeUtc (source, dateUtc);
			} catch (Exception ex) {
				Log.LogWarning ("There was a problem setting the Last Access/Write time on file {0}", source);
				Log.LogWarningFromException (ex);
			}
		}
#endif  // MSBUILD

		public static void SetWriteable (string source)
		{
			if (!File.Exists (source))
				return;

			var fileInfo = new FileInfo (source);
			if (fileInfo.IsReadOnly)
				fileInfo.IsReadOnly = false;
		}

		public static void SetDirectoryWriteable (string directory)
		{
			if (!Directory.Exists (directory))
				return;

			var dirInfo = new DirectoryInfo (directory);
			dirInfo.Attributes &= ~FileAttributes.ReadOnly;

			foreach (var dir in Directory.EnumerateDirectories (directory, "*", SearchOption.AllDirectories)) {
				dirInfo = new DirectoryInfo (dir);
				dirInfo.Attributes &= ~FileAttributes.ReadOnly;
			}

			foreach (var file in Directory.EnumerateFiles (directory, "*", SearchOption.AllDirectories)) {
				SetWriteable (Path.GetFullPath (file));
			}
		}

		public static bool CopyIfChanged (string source, string destination)
		{
			return Files.CopyIfChanged (source, destination);
		}

		public static bool CopyIfZipChanged (Stream source, string destination)
		{
			return Files.CopyIfZipChanged (source, destination);
		}

		public static bool CopyIfZipChanged (string source, string destination)
		{
			return Files.CopyIfZipChanged (source, destination);
		}

		public static ZipFile ReadZipFile (string filename)
		{
			return Files.ReadZipFile (filename);
		}

		public static string HashFile (string filename)
		{
			return Files.HashFile (filename);
		}

		public static string HashFile (string filename, HashAlgorithm hashAlg)
		{
			return Files.HashFile (filename, hashAlg);
		}

		public static string HashStream (Stream stream)
		{
			return Files.HashStream (stream);
		}

		/// <summary>
		/// Open a file given its path and remove the 3 bytes UTF-8 BOM if there is one
		/// </summary>
		public static void CleanBOM (string filePath)
		{
			if (string.IsNullOrEmpty (filePath) || !File.Exists (filePath))
				return;

			string outputFilePath = null;
			using (var input = File.OpenRead (filePath)) {
				// Check if the file actually has a BOM
				for (int i = 0; i < Utf8Preamble.Length; i++) {
					var next = input.ReadByte ();
					if (next == -1)
						return;
					if (Utf8Preamble [i] != (byte)next)
						return;
				}

				outputFilePath = Path.GetTempFileName ();
				using (var tempOutput = File.OpenWrite (outputFilePath))
					input.CopyTo (tempOutput);
			}

			CopyIfChanged (outputFilePath, filePath);
			try {
				File.Delete (outputFilePath);
			} catch {
			}
		}

		public static bool IsRawResourcePath (string projectPath)
		{
			// Extract resource type folder name
			var dir = Path.GetDirectoryName (projectPath);
			var name = Path.GetFileName (dir);

			return string.Equals (name, "raw", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith ("raw-", StringComparison.OrdinalIgnoreCase);
		}

#if MSBUILD
		internal static IEnumerable<string> GetFrameworkAssembliesToTreatAsUserAssemblies (ITaskItem[] resolvedAssemblies) 
		{		
			return resolvedAssemblies
				.Where (f => Array.BinarySearch (FrameworkAssembliesToTreatAsUserAssemblies, Path.GetFileName (f.ItemSpec), StringComparer.OrdinalIgnoreCase) >= 0)
				.Select(p => p.ItemSpec);
		}
#endif

		internal static readonly string [] FrameworkAttributeLookupTargets = {"Mono.Android.GoogleMaps.dll"};
		internal static readonly string [] FrameworkEmbeddedJarLookupTargets = {
			"Mono.Android.Support.v13.dll",
			"Mono.Android.Support.v4.dll",
			"Xamarin.Android.NUnitLite.dll", // AndroidResources
		};
		// MUST BE SORTED CASE-INSENSITIVE
		internal static readonly string[] FrameworkAssembliesToTreatAsUserAssemblies = {
			"Mono.Android.GoogleMaps.dll",
			"Mono.Android.Support.v13.dll",
			"Mono.Android.Support.v4.dll",
			"Xamarin.Android.NUnitLite.dll",
		};

		// It used to replace "21" with "L" when it was preview, or "23" with "MNC" (ditto).
		// We may have to use this in the future too.
		public static string GetPlatformApiLevelName (string platformApiLevel)
		{
			switch (platformApiLevel.Trim ()) {
			case "24":
				return "N";
			default:
				return platformApiLevel;
			}
		}

		// It used to replace "21" with "L" when it was preview, or "23" with "MNC" (ditto).
		// We may have to use this in the future too.
		public static string GetPlatformApiLevel (string platformApiLevelName)
		{
			switch (platformApiLevelName.Trim ()) {
			case "N":
				return "24";
			default:
				return platformApiLevelName;
			}
		}

		public static string GetLibraryImportDirectoryNameForAssembly (string assemblyIdentName)
		{
			return string.Concat (new MD2Managed ().ComputeHash (Encoding.UTF8.GetBytes (assemblyIdentName)).Take (5).Select (b => b.ToString ("X02")));
		}

		public static Dictionary<string, string> LoadAcwMapFile (string acwPath)
		{
			var acw_map = new Dictionary<string, string> ();
			if (!File.Exists (acwPath))
				return acw_map;
			foreach (var s in File.ReadLines (acwPath)) {
				var items = s.Split (';');
				if (!acw_map.ContainsKey (items [0]))
					acw_map.Add (items [0], items [1]);
			}
			return acw_map;
		}
	}
}
