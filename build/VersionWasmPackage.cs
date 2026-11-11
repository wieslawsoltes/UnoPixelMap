using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace PixelMap.Build
{
    /// <summary>
    /// Versions Uno's published bootstrap package from the completed deployment.
    /// Framework resources are only read: their SDK filenames and integrity hashes
    /// remain valid. Compression is a transport representation, not an identity input.
    /// </summary>
    public sealed class VersionWasmPackage : Task
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly string[] Adapters = { "audio/journey-audio.js", "input/map-input.js" };
        private static readonly Regex ScriptSource = new Regex(
            @"(?<prefix><script\b[^>]*?\bsrc\s*=\s*)(?<quote>[""'])(?<url>[^""']+)\k<quote>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex BootstrapPath = new Regex(
            @"(?:^|/)(?<package>package_[0-9a-f]{40,64})/uno-bootstrap\.js$",
            RegexOptions.CultureInvariant);
        private const string VersionParameter = "pixelmap-build";
        private const string CanonicalPackage = "package_CONTENT";

        [Required]
        public string WebRoot { get; set; }

        public string ProjectDirectory { get; set; }

        [Output]
        public string PackageName { get; set; }

        public override bool Execute()
        {
            try
            {
                var root = Path.GetFullPath(Path.Combine(ProjectDirectory ?? Environment.CurrentDirectory, WebRoot));
                var indexPath = Path.Combine(root, "index.html");
                var index = ReadText(indexPath);
                var oldPackage = FindPackage(index);
                var packagePath = Path.Combine(root, oldPackage);
                var frameworkPath = Path.Combine(root, "_framework");
                var bootstrapPath = Path.Combine(packagePath, "uno-bootstrap.js");
                var bootstrap = ReadText(bootstrapPath);
                // Bootstrap 10.1 imports ../uno-config.js from the hashed package,
                // keeping the publish-rewritten loader selection beside index.html.
                // Follow the selected bootstrapper, not an unrelated/stale config
                // from an incremental publish of the previous package layout.
                var rootConfig = bootstrap.IndexOf("../uno-config.js", StringComparison.Ordinal) >= 0;
                var configPath = Path.Combine(rootConfig ? root : packagePath, "uno-config.js");
                RequireFile(Path.Combine(packagePath, "require.js"));
                var config = ReadText(configPath);
                var appBase = Regex.Match(config, @"config\.uno_app_base\s*=\s*[""'](?<path>[^""']+)[""']");
                if (!appBase.Success || appBase.Groups["path"].Value.TrimEnd('/').Split('/').Last() != oldPackage)
                    throw new InvalidDataException("Published uno-config.js does not select the active bootstrap package.");
                var loader = Regex.Match(config, @"config\.dotnet_js_filename\s*=\s*[""'](?<file>dotnet(?:\.[a-z0-9]+)?\.js)[""']");
                if (!loader.Success)
                    throw new InvalidDataException("Published uno-config.js does not select a supported .NET loader.");
                RequireFile(Path.Combine(frameworkPath, loader.Groups["file"].Value));
                foreach (var adapter in Adapters)
                    RequireFile(Path.Combine(root, adapter));

                // Validate both adapter references before making any changes.
                RewriteAdapters(index, null);
                var packageReference = PackageReference(oldPackage);
                var text = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var file in LogicalFiles(packagePath).Where(IsText))
                    text.Add(file, ReadText(file));
                if (rootConfig)
                    text.Add(configPath, config);
                text.Add(indexPath, index);
                var workerPath = Path.Combine(root, "service-worker.js");
                if (File.Exists(workerPath))
                    text.Add(workerPath, ReadText(workerPath));

                var files = LogicalFiles(packagePath).Concat(LogicalFiles(frameworkPath))
                    .Concat(Adapters.Select(adapter => Path.Combine(root, adapter)))
                    .Concat(new[] { indexPath })
                    .Concat(rootConfig ? new[] { configPath } : new string[0])
                    .Concat(File.Exists(workerPath) ? new[] { workerPath } : new string[0])
                    .OrderBy(file => RelativePath(root, file), StringComparer.Ordinal).ToArray();
                var digest = ComputeIdentity(root, files, text, indexPath, oldPackage, packageReference);
                PackageName = "package_" + digest;
                var newPackagePath = Path.Combine(root, PackageName);

                var changes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var entry in text)
                {
                    var updated = packageReference.Replace(entry.Value, PackageName);
                    if (entry.Key == indexPath)
                        updated = RewriteAdapters(updated, digest);
                    if (updated != entry.Value)
                        changes.Add(entry.Key, Utf8.GetBytes(updated));
                }

                Commit(root, packagePath, newPackagePath, indexPath, changes);

                if (FindPackage(ReadText(indexPath)) != PackageName)
                    throw new InvalidDataException("Published index.html does not select the finalized package.");
                Log.LogMessage(MessageImportance.High, "PixelMap published package: {0} ({1} logical files)", PackageName, files.Length);
                return true;
            }
            catch (Exception exception)
            {
                Log.LogError("PixelMap publish versioning failed: {0}", exception.Message);
                return false;
            }
        }

        private void Commit(string root, string sourcePackage, string destinationPackage,
            string indexPath, Dictionary<string, byte[]> changes)
        {
            if (sourcePackage == destinationPackage && changes.Count == 0)
                return;

            // Validate before creating or moving anything. An incremental SDK publish
            // recreates its original package beside a previous finalized destination.
            // Logical conflicts are errors; transport sidecars come from this publish.
            var replacePackage = sourcePackage != destinationPackage;
            var existingDestination = replacePackage && Directory.Exists(destinationPackage);
            if (existingDestination)
                VerifyExistingPackage(sourcePackage, destinationPackage, changes);
            var rootChanges = changes.Where(entry => !IsWithin(sourcePackage, entry.Key))
                .OrderBy(entry => entry.Key == indexPath ? 1 : 0).ToArray();
            foreach (var entry in rootChanges)
            {
                if ((File.GetAttributes(entry.Key) & FileAttributes.ReadOnly) != 0)
                    throw new UnauthorizedAccessException("A published entry point is read-only: " + entry.Key);
                using (File.Open(entry.Key, FileMode.Open, FileAccess.Write, FileShare.Read)) { }
            }

            var transaction = Path.Combine(root, ".pixelmap-publish-" + Guid.NewGuid().ToString("N"));
            var stagedPackage = Path.Combine(transaction, "package");
            var previousPackage = Path.Combine(transaction, "previous-package");
            var replacedRoots = new List<KeyValuePair<string, string>>();
            var movedCompressedFiles = new List<KeyValuePair<string, string>>();
            var packageInstalled = false;
            var packageBackedUp = false;
            var completed = false;
            var rollbackSucceeded = true;
            Directory.CreateDirectory(transaction);
            try
            {
                // Stage every byte before touching entry points. Rebuilding a matching
                // destination also replaces any stale gzip/Brotli sidecars with the
                // current SDK representations, without changing framework resources.
                if (replacePackage)
                {
                    CopyDirectory(sourcePackage, stagedPackage);
                    foreach (var entry in changes.Where(entry => IsWithin(sourcePackage, entry.Key)))
                    {
                        var destination = Path.Combine(stagedPackage, RelativePath(sourcePackage, entry.Key));
                        File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
                        File.WriteAllBytes(destination, entry.Value);
                        DeleteIfExists(destination + ".gz");
                        DeleteIfExists(destination + ".br");
                    }
                }
                for (var index = 0; index < rootChanges.Length; index++)
                    File.WriteAllBytes(Path.Combine(transaction, "root-" + index), rootChanges[index].Value);

                if (existingDestination)
                {
                    Directory.Move(destinationPackage, previousPackage);
                    packageBackedUp = true;
                }
                if (replacePackage)
                {
                    Directory.Move(stagedPackage, destinationPackage);
                    packageInstalled = true;
                }
                for (var index = 0; index < rootChanges.Length; index++)
                {
                    var destination = rootChanges[index].Key;
                    var backup = Path.Combine(transaction, "original-" + index);
                    File.Replace(Path.Combine(transaction, "root-" + index), destination, backup);
                    replacedRoots.Add(new KeyValuePair<string, string>(destination, backup));
                    foreach (var extension in new[] { ".gz", ".br" })
                    {
                        var compressed = destination + extension;
                        if (!File.Exists(compressed))
                            continue;
                        var compressedBackup = backup + extension;
                        File.Move(compressed, compressedBackup);
                        movedCompressedFiles.Add(new KeyValuePair<string, string>(compressed, compressedBackup));
                    }
                }
                completed = true;
            }
            catch (Exception failure)
            {
                // In particular, restore the service-worker if a later index update
                // fails. Otherwise retrying against the old index could hash a mixed
                // set of old/new package references and produce a different identity.
                var errors = new List<Exception> { failure };
                foreach (var entry in movedCompressedFiles.AsEnumerable().Reverse())
                {
                    try { File.Move(entry.Value, entry.Key); }
                    catch (Exception rollback) { errors.Add(rollback); rollbackSucceeded = false; }
                }
                foreach (var entry in replacedRoots.AsEnumerable().Reverse())
                {
                    try { File.Replace(entry.Value, entry.Key, null); }
                    catch (Exception rollback) { errors.Add(rollback); rollbackSucceeded = false; }
                }
                if (rollbackSucceeded)
                {
                    try
                    {
                        if (packageInstalled)
                            DeleteOwnedDirectory(destinationPackage);
                        if (packageBackedUp)
                            Directory.Move(previousPackage, destinationPackage);
                    }
                    catch (Exception rollback) { errors.Add(rollback); rollbackSucceeded = false; }
                }
                if (!rollbackSucceeded)
                    Log.LogError("Publish rollback could not finish; recovery files remain at {0}.", transaction);
                throw new AggregateException("Could not commit finalized publish output.", errors);
            }
            finally
            {
                if (completed || rollbackSucceeded)
                    DeleteOwnedDirectory(transaction);
            }

            // Only the formerly active source is retired, and only after the entire
            // transaction committed. Unrelated/stale package folders stay untouched.
            if (replacePackage)
            {
                try { Directory.Delete(sourcePackage, true); }
                catch (IOException) { Log.LogMessage(MessageImportance.Low, "Retained former package: {0}", sourcePackage); }
                catch (UnauthorizedAccessException) { Log.LogMessage(MessageImportance.Low, "Retained former package: {0}", sourcePackage); }
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, RelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target);
            }
        }

        private static void DeleteOwnedDirectory(string directory)
        {
            if (!Directory.Exists(directory))
                return;
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(directory, true);
        }

        private static string ComputeIdentity(string root, string[] files, Dictionary<string, string> text,
            string indexPath, string oldPackage, Regex packageReference)
        {
            using (var identity = SHA256.Create())
            using (var contentHash = SHA256.Create())
            {
                Append(identity, Utf8.GetBytes("PixelMap.WasmPackage.v1"));
                foreach (var file in files)
                {
                    var path = RelativePath(root, file);
                    if (path.StartsWith(oldPackage + "/", StringComparison.Ordinal))
                        path = CanonicalPackage + path.Substring(oldPackage.Length);
                    Append(identity, Utf8.GetBytes(path));
                    byte[] hash;
                    string value;
                    if (text.TryGetValue(file, out value))
                    {
                        if (file == indexPath)
                            value = RewriteAdapters(value, null);
                        value = packageReference.Replace(value, CanonicalPackage);
                        hash = contentHash.ComputeHash(Utf8.GetBytes(value));
                    }
                    else
                    {
                        using (var stream = File.OpenRead(file))
                            hash = contentHash.ComputeHash(stream);
                    }
                    Append(identity, hash);
                }
                identity.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(identity.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void Append(HashAlgorithm hash, byte[] bytes)
        {
            // Length-prefix each field so paths and hashes cannot be concatenated
            // ambiguously. Explicit byte order keeps the digest platform-independent.
            var length = new[] { (byte)(bytes.Length >> 24), (byte)(bytes.Length >> 16), (byte)(bytes.Length >> 8), (byte)bytes.Length };
            hash.TransformBlock(length, 0, length.Length, null, 0);
            hash.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        private static void VerifyExistingPackage(string source, string destination, Dictionary<string, byte[]> changes)
        {
            var sourceFiles = LogicalFiles(source).ToArray();
            var sourcePaths = sourceFiles.Select(file => RelativePath(source, file)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            var destinationPaths = LogicalFiles(destination).Select(file => RelativePath(destination, file)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (!sourcePaths.SequenceEqual(destinationPaths))
                throw new InvalidDataException("An existing finalized package has unexpected files: " + destination);
            using (var hash = SHA256.Create())
            {
                foreach (var file in sourceFiles)
                {
                    byte[] content;
                    byte[] expected;
                    if (changes.TryGetValue(file, out content))
                        expected = hash.ComputeHash(content);
                    else
                    {
                        using (var stream = File.OpenRead(file))
                            expected = hash.ComputeHash(stream);
                    }
                    using (var stream = File.OpenRead(Path.Combine(destination, RelativePath(source, file))))
                    {
                        if (!hash.ComputeHash(stream).SequenceEqual(expected))
                            throw new InvalidDataException("An existing finalized package has unexpected content: " + destination);
                    }
                }
            }
        }

        private static string FindPackage(string index)
        {
            var packages = ScriptSource.Matches(index).Cast<Match>()
                .Select(match => BootstrapPath.Match(UrlPath(WebUtility.HtmlDecode(match.Groups["url"].Value))))
                .Where(match => match.Success).Select(match => match.Groups["package"].Value)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (packages.Length != 1)
                throw new InvalidDataException("Published index.html must select exactly one Uno bootstrap package.");
            return packages[0];
        }

        private static string RewriteAdapters(string index, string digest)
        {
            var counts = Adapters.ToDictionary(adapter => adapter, adapter => 0, StringComparer.Ordinal);
            var updated = ScriptSource.Replace(index, match =>
            {
                var url = WebUtility.HtmlDecode(match.Groups["url"].Value);
                var path = UrlPath(url);
                if (path.StartsWith("./", StringComparison.Ordinal))
                    path = path.Substring(2);
                if (!counts.ContainsKey(path))
                    return match.Value;
                counts[path]++;
                var fragmentIndex = url.IndexOf('#');
                var fragment = fragmentIndex < 0 ? "" : url.Substring(fragmentIndex);
                var address = fragmentIndex < 0 ? url : url.Substring(0, fragmentIndex);
                var queryIndex = address.IndexOf('?');
                var query = queryIndex < 0 ? new List<string>() : address.Substring(queryIndex + 1).Split('&')
                    .Where(part => part.Length != 0 && part.Split('=')[0] != VersionParameter).ToList();
                address = queryIndex < 0 ? address : address.Substring(0, queryIndex);
                if (digest != null)
                    query.Add(VersionParameter + "=" + digest);
                url = address + (query.Count == 0 ? "" : "?" + string.Join("&", query)) + fragment;
                return match.Groups["prefix"].Value + match.Groups["quote"].Value + WebUtility.HtmlEncode(url) + match.Groups["quote"].Value;
            });
            if (counts.Any(entry => entry.Value != 1))
                throw new InvalidDataException("Published index.html must reference each root browser adapter exactly once.");
            return updated;
        }

        private static string UrlPath(string url)
        {
            var end = url.IndexOfAny(new[] { '?', '#' });
            return end < 0 ? url : url.Substring(0, end);
        }

        private static Regex PackageReference(string package)
        {
            return new Regex(@"(?<![A-Za-z0-9_.-])" + Regex.Escape(package) + @"(?=$|[/\\""'\s?#])", RegexOptions.CultureInvariant);
        }

        private static IEnumerable<string> LogicalFiles(string directory)
        {
            return Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Where(file => !file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".br", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsText(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".html": case ".js": case ".mjs": case ".css": case ".json":
                case ".webmanifest": case ".txt": case ".svg": case ".xml":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsWithin(string directory, string file)
        {
            return file.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        private static string RelativePath(string directory, string file)
        {
            return file.Substring(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1).Replace(Path.DirectorySeparatorChar, '/');
        }

        private static string ReadText(string path)
        {
            RequireFile(path);
            // Decoding bytes directly preserves a possible UTF-8 BOM on rewrite.
            return Utf8.GetString(File.ReadAllBytes(path));
        }

        private static void RequireFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("A required published file is missing: " + path);
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
