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
    /// </summary>
    public sealed class WeatherPlugin : IMonitorSourcePlugin
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        private IPluginHost? _host;
        private CancellationTokenSource? _cts;
        private double _latitude = 31.2304;
        private double _longitude = 121.4737;
        private volatile string _text = "获取中…";

        /// <summary>宿主轮询频率（只读缓存，可以给得很高）。</summary>
        public int PollIntervalMs => 30000;

        public void Initialize(IPluginHost host)
        {
            _host = host;

            if (double.TryParse(host.GetSetting("latitude", "31.2304"), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat))
            {
                _latitude = lat;
            }

            if (double.TryParse(host.GetSetting("longitude", "121.4737"), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            {
                _longitude = lon;
            }

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _ = Task.Run(() => LoopAsync(token), token);
        }

        private async Task LoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);

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
            string city = _host?.GetSetting("city", "天气") ?? "天气";
            return new[] { new PluginMetric("weather", city, _text) };
        }

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
