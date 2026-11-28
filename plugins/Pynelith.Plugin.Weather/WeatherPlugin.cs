using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pynelith.Plugins;

namespace Pynelith.Plugin.Weather
{
    /// <summary>
    /// 天气插件（监控源）。
    /// 网络请求全部放在后台循环里，<see cref="Poll"/> 只读缓存，保证宿主按秒轮询时零阻塞。
    /// 城市默认按公网 IP 自动定位，每 6 小时复核一次；在 plugin.json 里显式填写
    /// latitude / longitude 即视为手动指定，不再自动定位。
    /// </summary>
    public sealed class WeatherPlugin : IMonitorSourcePlugin
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        /// <summary>旧版本写死的上海坐标：当作"未指定"，用于升级后自动切到 IP 定位。</summary>
        private const double LegacyLatitude = 31.2304;
        private const double LegacyLongitude = 121.4737;

        private static readonly TimeSpan LocateInterval = TimeSpan.FromHours(6);

        private IPluginHost? _host;
        private CancellationTokenSource? _cts;
        private bool _manualLocation;
        private double _latitude = double.NaN;
        private double _longitude = double.NaN;
        private string _city = string.Empty;
        private DateTime _locatedUtc = DateTime.MinValue;
        private volatile string _text = "获取中…";

        /// <summary>宿主轮询频率（只读缓存，可以给得很高）。</summary>
        public int PollIntervalMs => 30000;

        public void Initialize(IPluginHost host)
        {
            _host = host;
            _city = host.GetSetting("city", string.Empty).Trim();

            if (TryReadManualLocation(host, out double latitude, out double longitude))
            {
                _manualLocation = true;
                _latitude = latitude;
                _longitude = longitude;
            }

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _ = Task.Run(() => LoopAsync(token), token);
        }

        /// <summary>只有显式填写且不是旧版上海默认值的坐标才算手动指定。</summary>
        private static bool TryReadManualLocation(IPluginHost host, out double latitude, out double longitude)
        {
            latitude = 0;
            longitude = 0;

            if (!double.TryParse(
                    host.GetSetting("latitude", string.Empty),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double lat))
            {
                return false;
            }

            if (!double.TryParse(
                    host.GetSetting("longitude", string.Empty),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double lon))
            {
                return false;
            }

            bool isLegacyDefault =
                Math.Abs(lat - LegacyLatitude) < 0.0001 &&
                Math.Abs(lon - LegacyLongitude) < 0.0001;

            if (isLegacyDefault)
            {
                return false;
            }

            latitude = lat;
            longitude = lon;
            return true;
        }

        private bool HasLocation =>
            !double.IsNaN(_latitude) && !double.IsNaN(_longitude);

        private async Task LoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_manualLocation &&
                    (!HasLocation || DateTime.UtcNow - _locatedUtc > LocateInterval))
                {
                    await LocateByIpAsync(cancellationToken).ConfigureAwait(false);
                }

                if (HasLocation)
                {
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>按公网 IP 定位城市。三家服务依次尝试，全部失败保留上次结果。</summary>
        private async Task LocateByIpAsync(CancellationToken cancellationToken)
        {
            (string City, double Latitude, double Longitude)? location = null;

            try
            {
                location = await LocateFromIpApiAsync(cancellationToken).ConfigureAwait(false)
                    ?? await LocateFromIpWhoIsAsync(cancellationToken).ConfigureAwait(false)
                    ?? await LocateFromIpApiCoAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _host?.Log("IP 定位失败：" + exception.Message);
            }

            if (location == null)
            {
                _host?.Log("IP 定位失败，10 分钟后重试");
                if (!HasLocation)
                {
                    _text = "定位失败";
                    Publish();
                }

                return;
            }

            _latitude = location.Value.Latitude;
            _longitude = location.Value.Longitude;
            _locatedUtc = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(location.Value.City))
            {
                _city = location.Value.City;
            }

            _host?.Log("IP 定位成功：" + (_city.Length == 0 ? "未知城市" : _city));
            Publish();
        }

        /// <summary>ip-api.com：支持中文城市名（免费版仅 HTTP）。</summary>
        private static async Task<(string City, double Latitude, double Longitude)?> LocateFromIpApiAsync(
            CancellationToken cancellationToken)
        {
            const string url =
                "http://ip-api.com/json/?lang=zh-CN&fields=status,city,lat,lon";

            string json = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("status", out JsonElement status) ||
                !string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!TryReadNumber(root, "lat", out double latitude) ||
                !TryReadNumber(root, "lon", out double longitude))
            {
                return null;
            }

            return (ReadString(root, "city"), latitude, longitude);
        }

        /// <summary>ipwho.is：HTTPS 免费服务，作为首选失败后的备选。</summary>
        private static async Task<(string City, double Latitude, double Longitude)?> LocateFromIpWhoIsAsync(
            CancellationToken cancellationToken)
        {
            const string url = "https://ipwho.is/?lang=zh-CN";

            string json = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("success", out JsonElement success) &&
                success.ValueKind == JsonValueKind.False)
            {
                return null;
            }

            if (!TryReadNumber(root, "latitude", out double latitude) ||
                !TryReadNumber(root, "longitude", out double longitude))
            {
                return null;
            }

            return (ReadString(root, "city"), latitude, longitude);
        }

        /// <summary>ipapi.co：再一层 HTTPS 兜底。</summary>
        private static async Task<(string City, double Latitude, double Longitude)?> LocateFromIpApiCoAsync(
            CancellationToken cancellationToken)
        {
            const string url = "https://ipapi.co/json/";

            string json = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("error", out JsonElement error) &&
                error.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            if (!TryReadNumber(root, "latitude", out double latitude) ||
                !TryReadNumber(root, "longitude", out double longitude))
            {
                return null;
            }

            return (ReadString(root, "city"), latitude, longitude);
        }

        private static string ReadString(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out JsonElement value) &&
                   value.ValueKind == JsonValueKind.String
                ? (value.GetString() ?? string.Empty).Trim()
                : string.Empty;
        }

        private static bool TryReadNumber(JsonElement element, string property, out double value)
        {
            value = 0;

            if (!element.TryGetProperty(property, out JsonElement node))
            {
                return false;
            }

            if (node.ValueKind == JsonValueKind.Number)
            {
                return node.TryGetDouble(out value);
            }

            if (node.ValueKind == JsonValueKind.String)
            {
                return double.TryParse(
                    node.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value);
            }

            return false;
        }

        private async Task RefreshAsync(CancellationToken cancellationToken)
        {
            string url = string.Format(
                CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,weather_code&timezone=auto",
                _latitude,
                _longitude);

            try
            {
                string json = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement current = document.RootElement.GetProperty("current");
                    double temperature = current.GetProperty("temperature_2m").GetDouble();
                    int code = current.GetProperty("weather_code").GetInt32();

                    _text = Math.Round(temperature).ToString("0", CultureInfo.InvariantCulture)
                        + "°C " + Describe(code);
                    Publish();
                    _host?.Log("天气已更新：" + _text);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 断网 / 接口变更：保留上次结果，只记日志
                _host?.Log("天气获取失败：" + exception.Message);
            }
        }

        public IReadOnlyList<PluginMetric> Poll()
        {
            return new[] { new PluginMetric("weather", DisplayCity, _text) };
        }

        /// <summary>指标标签：定位成功前用「天气」占位。</summary>
        private string DisplayCity => string.IsNullOrWhiteSpace(_city) ? "天气" : _city;

        /// <summary>
        /// 主动推一次指标。宿主轮询间隔 30 秒，定位/刷新完成后立刻推送，
        /// 用户不用等下一轮轮询才能看到城市和气温。
        /// </summary>
        private void Publish() => _host?.UpdateMetric("weather", DisplayCity, _text);

        /// <summary>WMO 天气代码 → 中文短句（只保留常见取值）。</summary>
        private static string Describe(int code)
        {
            if (code == 0)
            {
                return "晴";
            }

            if (code <= 2)
            {
                return "多云";
            }

            if (code == 3)
            {
                return "阴";
            }

            if (code == 45 || code == 48)
            {
                return "雾";
            }

            if (code >= 51 && code <= 57)
            {
                return "毛毛雨";
            }

            if (code >= 61 && code <= 67)
            {
                return "雨";
            }

            if (code >= 71 && code <= 77)
            {
                return "雪";
            }

            if (code >= 80 && code <= 82)
            {
                return "阵雨";
            }

            if (code >= 85 && code <= 86)
            {
                return "阵雪";
            }

            if (code >= 95)
            {
                return "雷雨";
            }

            return "未知";
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _host = null;
        }
    }
}
