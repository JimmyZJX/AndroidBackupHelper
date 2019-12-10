using CommandLine;
using CommandLine.Text;
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.IO;

using File = Alphaleonis.Win32.Filesystem.File;
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using Path = Alphaleonis.Win32.Filesystem.Path;

namespace AndroidBackupHelper
{
    [Verb("unpack", HelpText = "Unpack an android backup file (*.ab).")]
    public class UnpackOptions
    {
        [Value(0, Required = true)]
        public string file { get; set; }

        [Value(1, Required = false, Default = "")]
        public string directory { get; set; }
    }

    [Verb("show", HelpText = "List content of an android backup file (*.ab).")]
    public class ShowOptions
    {
        [Value(0, Required = true)]
        public string file { get; set; }
    }

    [Verb("pack", HelpText = "pack a backup \"apps\" dir.")]
    public class PackOptions
    {
        [Value(0, Required = true)]
        public string apps_dir { get; set; }

        [Value(1, Required = true)]
        public string file { get; set; }

        [Option('C', Required = false, HelpText = "Disable Android backup conventions")]
        public bool disableConventions { get; set; }

        [Usage(ApplicationAlias = "AndroidBackupHelper")]
        public static IEnumerable<Example> Examples =>
            new List<Example>() {
                new Example("Pack your backup at \"C:\\somedir\\apps\\\"", new PackOptions { apps_dir = @"C:\somedir\apps", file = "myApp.ab" })
            };
    }

    class Program
    {
        static int Main(string[] args) {

            return Parser.Default.ParseArguments<UnpackOptions, ShowOptions, PackOptions>(args)
                .MapResult(
                    (UnpackOptions opts) => {
                        using (var tarStream = GetTarInputStream(File.OpenRead(opts.file))) {
                            ExtractTarByEntry(tarStream, opts.directory);
                        }
                        // tarArchive.ExtractContents(opts.directory);

                        return 0;
                    },
                    (ShowOptions opts) => {
                        using (var tarStream = GetTarInputStream(File.OpenRead(opts.file))) {

                            TarEntry tarEntry;
                            while ((tarEntry = tarStream.GetNextEntry()) != null) {
                                var entryStr = tarEntry.IsDirectory ? "  [D] " : "  [F] ";

                                entryStr += $"{tarEntry.Name} {tarEntry.Size:n0} bytes";

                                Console.WriteLine(entryStr);
                            }
                        }

                        return 0;
                    },
                    (PackOptions opts) => {
                        using (var outAB = File.OpenWrite(opts.file)) {
                            outputAndroidBackupHeader(outAB);

                            using (var defOut = new DeflaterOutputStream(outAB))
                            using (var tarOutputStream = new TarOutputStream(defOut)) {
                                AddAppsToTar(tarOutputStream, opts.apps_dir, !opts.disableConventions);
                            }
                        }

                        return 0;
                    },
                    errs => 1);

        }

        /// </summary>
        // Iterates through each file entry within the supplied tar,
        // extracting them to the nominated folder.
        /// </summary>
        public static void ExtractTarByEntry(TarInputStream tarIn, string targetDir) {
            TarEntry tarEntry;
            while ((tarEntry = tarIn.GetNextEntry()) != null) {

                // Converts the unix forward slashes in the filenames to windows backslashes
                string name = tarEntry.Name.Replace('/', Path.DirectorySeparatorChar);

                // Remove any root e.g. '\' because a PathRooted filename defeats Path.Combine
                if (Path.IsPathRooted(name))
                    name = name.Substring(System.IO.Path.GetPathRoot(name).Length);

                // Apply further name transformations here as necessary
                string outName = Path.Combine(targetDir, name);

                string directoryName = Path.GetDirectoryName(outName);

                try {
                    if (tarEntry.IsDirectory) {
                        Directory.CreateDirectory(outName);
                        continue;
                    }

                    // Does nothing if directory exists
                    Directory.CreateDirectory(directoryName);

                    try {
                        using (var outStr = File.Open(outName, FileMode.Create)) {
                            tarIn.CopyEntryContents(outStr);
                        }

                        // Set the modification date/time. This approach seems to solve timezone issues.
                        DateTime myDt = DateTime.SpecifyKind(tarEntry.ModTime, DateTimeKind.Utc);
                        File.SetLastWriteTime(outName, myDt);
                    } catch (NotSupportedException) {
                        Console.WriteLine($"[!] invalid file name: {outName}");
                    } catch (PathTooLongException) {
                        Console.WriteLine($"[!] file name too long?! {outName}");
                    }
                } catch (NotSupportedException) {
                    Console.WriteLine($"[!] invalid directory name: {directoryName}");
                }
            }
        }

        public static string PathCombineUnixUnsafe(string path1, string path2) {
            if (path1 == null || path2 == null)
                throw new Exception("PathCombine: null");

            if (path2.Length == 0) return path1;
            if (path1.Length == 0) return path2;

            return $"{path1.Trim('/', '\\')}/{path2.Trim('/', '\\')}";
        }

        public static Dictionary<string, string> AndroidBackupConventions = new Dictionary<string, string> {
            { "databases", "db" },
            { "files", "f" },
            { "shared_prefs", "sp" },
            { "cache", "c" },
            { "__sdcard_files__", "ef" },
            { "__apk__", "a" },
            { "__obb__", "obb" },
            { "__wildcard__", "r" },
        };
        public static string AndroidBackupWildcardDir = "r";

        static void AddAppsToTar(TarOutputStream tarOutputStream, string apps_directory, bool conventions) {

            string[] directories = Directory.GetDirectories(apps_directory);

            foreach (string directory in directories) {
                var app = Path.GetFileName(directory);
                Console.WriteLine($"[+] App: {app}");

                // manifest comes first
                using (var fin_manifest = File.OpenRead(Path.Combine(directory, "_manifest"))) {
                    AddFileToTarRaw(tarOutputStream, fin_manifest, $"apps/{app}/_manifest");
                }

                // write other files
                foreach (var directory2 in Directory.GetDirectories(directory)) {
                    var dir2 = Path.GetFileName(directory2);

                    if (conventions) {
                        if (AndroidBackupConventions.TryGetValue(dir2, out var dir2Real)) {
                            Console.WriteLine("[+]    {dir2} -> {dir2Real}");
                            dir2 = dir2Real;
                        } else if (AndroidBackupConventions.ContainsValue(dir2)) {
                            // name accepted
                        } else {
                            dir2Real = $"{AndroidBackupWildcardDir}/{dir2}";
                            Console.WriteLine("[!]    {dir2} -> {dir2Real}");
                            dir2 = dir2Real;
                        }
                    }

                    Console.WriteLine($"[+]    - {dir2}");
                    AddDirToTar(tarOutputStream, directory2, $"apps/{app}/{dir2}", false);
                }
            }
        }

        static void AddDirToTar(TarOutputStream tarOutputStream, string sourceDirectory, string basePath, bool writeDirEntry = true) {
            // Optionally, write an entry for the directory itself.
            if (writeDirEntry) {
                TarEntry tarEntry = TarEntry.CreateEntryFromFile(sourceDirectory);
                tarEntry.Name = basePath;
                tarOutputStream.PutNextEntry(tarEntry);
            }

            // Write each file to the tar.
            string[] filenames = Directory.GetFiles(sourceDirectory);

            foreach (string filename in filenames) {
                using (Stream inputStream = File.OpenRead(filename)) {
                    AddFileToTarRaw(tarOutputStream, inputStream,
                        PathCombineUnixUnsafe(basePath, Path.GetFileName(filename)));
                }
            }

            // Recurse.
            string[] directories = Directory.GetDirectories(sourceDirectory);
            foreach (string directory in directories)
                AddDirToTar(tarOutputStream, directory,
                    PathCombineUnixUnsafe(basePath, Path.GetFileName(directory)));
        }

        static void AddFileToTarRaw(TarOutputStream tarOutputStream, Stream fin, string name) {

            long fileSize = fin.Length;

            // Create a tar entry named as appropriate. You can set the name to anything,
            // but avoid names starting with drive or UNC.
            TarEntry entry = TarEntry.CreateTarEntry(name);

            // Must set size, otherwise TarOutputStream will fail when output exceeds.
            entry.Size = fileSize;

            // Add the entry to the tar stream, before writing the data.
            tarOutputStream.PutNextEntry(entry);

            // this is copied from TarArchive.WriteEntryCore
            byte[] localBuffer = new byte[32 * 1024];
            while (true) {
                int numRead = fin.Read(localBuffer, 0, localBuffer.Length);
                if (numRead <= 0)
                    break;

                tarOutputStream.Write(localBuffer, 0, numRead);
            }

            tarOutputStream.CloseEntry();
        }

        public static TarInputStream GetTarInputStream(Stream fin) {
            skipAndroidBackupHeader(fin);

            InflaterInputStream inflater = new InflaterInputStream(fin);
            return new TarInputStream(inflater);
        }

        public static TarArchive GetTarArchive(Stream fin) {
            skipAndroidBackupHeader(fin);

            InflaterInputStream inflater = new InflaterInputStream(fin);
            return TarArchive.CreateInputTarArchive(inflater);
        }

        public const string ANDROIDBACKUP_HEADER = "ANDROID BACKUP";
        public static void skipAndroidBackupHeader(Stream fin) {
            if (fin.CanRead) {
                int headerLen = ANDROIDBACKUP_HEADER.Length;
                byte[] headerBuf = new byte[headerLen];

                if (fin.Read(headerBuf, 0, headerLen) != headerLen)
                    throw new Exception("Invalid header: length");

                if (Encoding.ASCII.GetString(headerBuf) != ANDROIDBACKUP_HEADER)
                    throw new Exception("Invalid header: neq");

                for (int i = 0; i < 4; i++) {
                    while (fin.ReadByte() != 0x0A) ;
                }

                // Console.WriteLine($"Header len = {fin.Position}");
            } else {
                throw new Exception("Stream not readable");
            }
        }

        public static void outputAndroidBackupHeader(Stream fout) {
            byte[] newline = new byte[] { 0x0A };
            byte[] header = Encoding.ASCII.GetBytes(ANDROIDBACKUP_HEADER)
                .Concat(newline)
                .Concat(Encoding.ASCII.GetBytes("5"))
                .Concat(newline)
                .Concat(Encoding.ASCII.GetBytes("1"))
                .Concat(newline)
                .Concat(Encoding.ASCII.GetBytes("none"))
                .Concat(newline)
                .ToArray();
            fout.Write(header, 0, header.Length);
        }
    }
}
