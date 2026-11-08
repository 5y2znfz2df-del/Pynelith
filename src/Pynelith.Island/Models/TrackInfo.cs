using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Pynelith.Island.Models
{
    /// <summary>正在播放的曲目与当前歌词行（模拟数据）。</summary>
    public sealed class TrackInfo : INotifyPropertyChanged
    {
        private string _title = "Ambient Loop";
        private string _artist = "Pynelith Studio";
        private string _lyric = "夜色在玻璃边缘缓缓流过";
        private bool _isPlaying = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Title
        {
            get => _title;
            set => Set(ref _title, value);
        }

        public string Artist
        {
            get => _artist;
            set => Set(ref _artist, value);
        }

        /// <summary>当前歌词行。</summary>
        public string Lyric
        {
            get => _lyric;
            set => Set(ref _lyric, value);
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            set => Set(ref _isPlaying, value);
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
