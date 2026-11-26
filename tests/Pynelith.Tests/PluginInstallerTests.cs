using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Pynelith.Island.Services.Plugins;
using Xunit;

namespace Pynelith.Tests
{
    /// <summary>插件导入：zip 解包、目录落地、清单校验、SHA-256 校验。</summary>
    public sealed class PluginInstallerTests : IDisposable
    {
        private const string ManifestJson =
            "{ \"id\": \"demo\", \"name\": \"演示\", \"version\": \"1.0.0\", \"author\": \"tests\","
            + " \"kind\": \"MonitorSource\", \"entryAssembly\": \"Demo.dll\", \"entryType\": \"Demo.Plugin\" }";

        private readonly string _root;
        private readonly string _userRoot;

        public PluginInstallerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "pynelith-installer-" + Guid.NewGuid().ToString("N"));
            _userRoot = Path.Combine(_root, "plugins");
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void ImportZip_ManifestAtRoot_Succeeds()
        {
            string zip = CreateZip("demo.zip", "plugin.json", ManifestJson, "Demo.dll", "MZ-fake");

            PluginImportResult result = PluginInstaller.ImportZip(zip, _userRoot);

            Assert.True(result.Success, result.Error);
            Assert.Equal("demo", result.PluginId);
            Assert.True(File.Exists(Path.Combine(_userRoot, "demo", "plugin.json")));
            Assert.True(File.Exists(Path.Combine(_userRoot, "demo", "Demo.dll")));
        }

        [Fact]
        public void ImportZip_ManifestInOneSubdirectory_Succeeds()
        {
            string zip = CreateZip("nested.zip", "demo/plugin.json", ManifestJson, "demo/Demo.dll", "MZ-fake");

            PluginImportResult result = PluginInstaller.ImportZip(zip, _userRoot);

            Assert.True(result.Success, result.Error);
            Assert.True(File.Exists(Path.Combine(_userRoot, "demo", "Demo.dll")));
        }

        [Fact]
        public void ImportZip_WithoutManifest_Fails()
        {
            string zip = CreateZip("empty.zip", "readme.txt", "no manifest here");

            PluginImportResult result = PluginInstaller.ImportZip(zip, _userRoot);

            Assert.False(result.Success);
            Assert.Contains("plugin.json", result.Error);
        }

        [Fact]
        public void ImportDirectory_RejectsIllegalManifest()
        {
            string source = Path.Combine(_root, "bad");
            Directory.CreateDirectory(source);
            File.WriteAllText(
                Path.Combine(source, "plugin.json"),
                "{ \"id\": \"Bad Id\", \"name\": \"坏\", \"entryAssembly\": \"a.dll\", \"entryType\": \"A.B\" }");

            PluginImportResult result = PluginInstaller.ImportDirectory(source, _userRoot);

            Assert.False(result.Success);
            Assert.False(Directory.Exists(Path.Combine(_userRoot, "Bad Id")));
        }

        [Fact]
        public void ImportDirectory_ReplacesExistingVersion()
        {
            string first = Path.Combine(_root, "v1");
            Directory.CreateDirectory(first);
            File.WriteAllText(Path.Combine(first, "plugin.json"), ManifestJson);
            File.WriteAllText(Path.Combine(first, "old.txt"), "old");

            Assert.True(PluginInstaller.ImportDirectory(first, _userRoot).Success);

            string second = Path.Combine(_root, "v2");
            Directory.CreateDirectory(second);
            File.WriteAllText(Path.Combine(second, "plugin.json"), ManifestJson);
            File.WriteAllText(Path.Combine(second, "new.txt"), "new");

            Assert.True(PluginInstaller.ImportDirectory(second, _userRoot).Success);

            string target = Path.Combine(_userRoot, "demo");
            Assert.True(File.Exists(Path.Combine(target, "new.txt")));
            Assert.False(File.Exists(Path.Combine(target, "old.txt")), "覆盖安装应清掉旧版本残留文件");
        }

        [Fact]
        public void VerifySha256_MatchesAndRejects()
        {
            string file = Path.Combine(_root, "Demo.dll");
            File.WriteAllText(file, "MZ-fake");

            using var sha = SHA256.Create();
            using FileStream stream = File.OpenRead(file);
            string expected = Convert.ToHexString(sha.ComputeHash(stream));

            Assert.True(PluginInstaller.VerifySha256(file, expected));
            Assert.True(PluginInstaller.VerifySha256(file, expected.ToLowerInvariant()));
            Assert.False(PluginInstaller.VerifySha256(file, new string('0', 64)));
        }

        [Fact]
        public void VerifySha256_EmptyExpectationSkipsCheck()
        {
            string file = Path.Combine(_root, "Demo.dll");
            File.WriteAllText(file, "MZ-fake");

            Assert.True(PluginInstaller.VerifySha256(file, string.Empty));
        }

        [Fact]
        public void VerifySha256_MissingFileFails()
        {
            Assert.False(PluginInstaller.VerifySha256(Path.Combine(_root, "nope.dll"), new string('a', 64)));
        }

        private string CreateZip(string name, string firstEntry, string firstContent, string secondEntry = "", string secondContent = "")
        {
            string path = Path.Combine(_root, name);

            using (FileStream stream = File.Create(path))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteEntry(archive, firstEntry, firstContent);

                if (!string.IsNullOrEmpty(secondEntry))
                {
                    WriteEntry(archive, secondEntry, secondContent);
                }
            }

            return path;
        }

        private static void WriteEntry(ZipArchive archive, string entryName, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName);
            using StreamWriter writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }
}
