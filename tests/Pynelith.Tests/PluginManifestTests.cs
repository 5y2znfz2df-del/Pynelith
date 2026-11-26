using System;
using Pynelith.Plugins;
using Xunit;

namespace Pynelith.Tests
{
    /// <summary>插件清单校验：合法清单通过，非法字段给出可读原因。</summary>
    public sealed class PluginManifestTests
    {
        private static PluginManifest Valid()
            => new PluginManifest
            {
                Id = "weather",
                Name = "天气",
                Version = "1.0.0",
                Author = "Pynelith",
                Kind = PluginKind.MonitorSource,
                EntryAssembly = "Pynelith.Plugin.Weather.dll",
                EntryType = "Pynelith.Plugin.Weather.WeatherPlugin",
            };

        [Fact]
        public void Validate_AcceptsValidManifest()
        {
            Valid().Validate();
        }

        [Fact]
        public void Validate_AllowsDotsAndDigitsInId()
        {
            PluginManifest manifest = Valid();
            manifest.Id = "vendor.plugin-2";

            manifest.Validate();
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Validate_RejectsEmptyId(string id)
        {
            PluginManifest manifest = Valid();
            manifest.Id = id;

            Assert.Throws<PluginException>(() => manifest.Validate());
        }

        [Theory]
        [InlineData("Weather")]
        [InlineData("天气")]
        [InlineData("has space")]
        [InlineData("under_score")]
        public void Validate_RejectsIllegalCharactersInId(string id)
        {
            PluginManifest manifest = Valid();
            manifest.Id = id;

            Assert.Throws<PluginException>(() => manifest.Validate());
        }

        [Fact]
        public void Validate_RejectsOverlongId()
        {
            PluginManifest manifest = Valid();
            manifest.Id = new string('a', 65);

            Assert.Throws<PluginException>(() => manifest.Validate());
        }

        [Fact]
        public void Validate_RejectsMissingName()
        {
            PluginManifest manifest = Valid();
            manifest.Name = string.Empty;

            Assert.Throws<PluginException>(() => manifest.Validate());
        }

        [Fact]
        public void Validate_RejectsUnknownKind()
        {
            PluginManifest manifest = Valid();
            manifest.Kind = (PluginKind)99;

            Assert.Throws<PluginException>(() => manifest.Validate());
        }

        [Fact]
        public void Validate_RejectsMissingEntryAssembly()
        {
            PluginManifest manifest = Valid();
            manifest.EntryAssembly = string.Empty;

            Assert.Throws<PluginException>(() => manifest.Validate());
        }

        [Fact]
        public void Validate_RejectsMissingEntryType()
        {
            PluginManifest manifest = Valid();
            manifest.EntryType = string.Empty;

            Assert.Throws<PluginException>(() => manifest.Validate());
        }

        [Fact]
        public void Defaults_AreBackwardCompatible()
        {
            // 老插件不写这些字段时也应能通过校验
            var manifest = new PluginManifest
            {
                Id = "legacy",
                Name = "老插件",
                EntryAssembly = "Legacy.dll",
                EntryType = "Legacy.Plugin",
            };

            manifest.Validate();

            Assert.Equal("1.0.0", manifest.Version);
            Assert.Equal("0.3.0", manifest.MinHostVersion);
            Assert.Equal(PluginKind.MonitorSource, manifest.Kind);
            Assert.Empty(manifest.Settings);
        }
    }
}
