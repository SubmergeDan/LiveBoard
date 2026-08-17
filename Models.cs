using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.Serialization;

namespace LiveBoard
{
    [DataContract]
    public sealed class RoomConfig : INotifyPropertyChanged
    {
        private string _remark;
        private string _quality;
        private string _outputFormat;
        private string _segmentMode;
        private int _segmentMinutes;
        private int _segmentSizeMb;
        private bool _autoRecordEnabled;
        private string _platform;
        private ObservableCollection<string> _availableQualities = new ObservableCollection<string>();

        [DataMember(Order = 1)]
        public string RoomId { get; set; }

        [DataMember(Order = 2)]
        public string Remark
        {
            get { return _remark; }
            set
            {
                if (SetRuntimeValue(ref _remark, value, "Remark"))
                    RaisePropertyChanged("DisplayName");
            }
        }

        [DataMember(Order = 3)]
        public string Quality
        {
            get { return _quality; }
            set
            {
                if (SetRuntimeValue(ref _quality, value, "Quality"))
                    RaisePropertyChanged("QualityLabel");
            }
        }

        [DataMember(Order = 4)]
        public string OutputFormat
        {
            get { return _outputFormat; }
            set { SetRuntimeValue(ref _outputFormat, value, "OutputFormat"); }
        }

        [DataMember(Order = 5)]
        public bool SegmentEnabled { get; set; }

        [DataMember(Order = 6)]
        public string SegmentMode
        {
            get { return _segmentMode; }
            set
            {
                if (SetRuntimeValue(ref _segmentMode, value, "SegmentMode"))
                    RaisePropertyChanged("SegmentModeLabel");
            }
        }

        [DataMember(Order = 7)]
        public int SegmentMinutes
        {
            get { return _segmentMinutes; }
            set
            {
                if (SetRuntimeValue(ref _segmentMinutes, value, "SegmentMinutes"))
                    RaisePropertyChanged("SegmentModeLabel");
            }
        }

        [DataMember(Order = 8)]
        public int SegmentSizeMb
        {
            get { return _segmentSizeMb; }
            set
            {
                if (SetRuntimeValue(ref _segmentSizeMb, value, "SegmentSizeMb"))
                    RaisePropertyChanged("SegmentModeLabel");
            }
        }

        [DataMember(Order = 9)]
        public bool AutoRecordEnabled
        {
            get { return _autoRecordEnabled; }
            set { SetRuntimeValue(ref _autoRecordEnabled, value, "AutoRecordEnabled"); }
        }

        [DataMember(Order = 10)]
        public string Platform
        {
            get { return _platform; }
            set
            {
                if (SetRuntimeValue(ref _platform, value, "Platform"))
                    RaisePropertyChanged("PlatformLabel");
            }
        }

        private string _liveStatus = "待检测";
        private string _liveStatusDetail = "等待检查";
        private bool _isRecording;
        private string _recordingStatus = "待命";
        private string _recordingElapsed = string.Empty;
        private bool _isSelected;
        private int _consecutiveOfflineChecks;
        private bool _autoRecordSuppressed;

        [IgnoreDataMember]
        public string LiveStatus
        {
            get { return _liveStatus; }
            set { SetRuntimeValue(ref _liveStatus, value, "LiveStatus"); }
        }

        [IgnoreDataMember]
        public string LiveStatusDetail
        {
            get { return _liveStatusDetail; }
            set { SetRuntimeValue(ref _liveStatusDetail, value, "LiveStatusDetail"); }
        }

        [IgnoreDataMember]
        public bool IsRecording
        {
            get { return _isRecording; }
            set { SetRuntimeValue(ref _isRecording, value, "IsRecording"); }
        }

        [IgnoreDataMember]
        public string RecordingStatus
        {
            get { return _recordingStatus; }
            set { SetRuntimeValue(ref _recordingStatus, value, "RecordingStatus"); }
        }

        [IgnoreDataMember]
        public string RecordingElapsed
        {
            get { return _recordingElapsed; }
            set { SetRuntimeValue(ref _recordingElapsed, value, "RecordingElapsed"); }
        }

        [IgnoreDataMember]
        public bool IsSelected
        {
            get { return _isSelected; }
            set { SetRuntimeValue(ref _isSelected, value, "IsSelected"); }
        }

        [IgnoreDataMember]
        public int ConsecutiveOfflineChecks
        {
            get { return _consecutiveOfflineChecks; }
            set { _consecutiveOfflineChecks = value; }
        }

        [IgnoreDataMember]
        public ObservableCollection<string> AvailableQualities
        {
            get
            {
                if (_availableQualities == null)
                    _availableQualities = new ObservableCollection<string>();
                return _availableQualities;
            }
        }

        [IgnoreDataMember]
        public bool AutoRecordSuppressed
        {
            get { return _autoRecordSuppressed; }
            set { _autoRecordSuppressed = value; }
        }

        [IgnoreDataMember]
        public string DisplayName
        {
            get { return string.IsNullOrWhiteSpace(Remark) ? "正在获取主播信息" : Remark; }
        }

        [IgnoreDataMember]
        public string QualityLabel
        {
            get { return string.IsNullOrWhiteSpace(Quality) ? "自动" : Quality; }
        }

        [IgnoreDataMember]
        public string PlatformLabel
        {
            get { return string.Equals(Platform, "Bilibili", StringComparison.OrdinalIgnoreCase) ? "Bilibili" : "抖音"; }
        }

        [IgnoreDataMember]
        public string SegmentModeLabel
        {
            get
            {
                var mode = string.IsNullOrWhiteSpace(SegmentMode) ? (SegmentEnabled ? "时间" : "关闭") : SegmentMode;
                if (mode == "时间")
                    return "每 " + (SegmentMinutes <= 0 ? 60 : SegmentMinutes) + " 分钟";
                if (mode == "大小")
                    return "每 " + (SegmentSizeMb <= 0 ? 2048 : SegmentSizeMb) + " MB";
                return "不分片";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private bool SetRuntimeValue<T>(ref T field, T value, string propertyName)
        {
            if (Equals(field, value))
                return false;
            field = value;
            RaisePropertyChanged(propertyName);
            return true;
        }

        private void RaisePropertyChanged(string propertyName)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
        }

        public RoomConfig Clone()
        {
            return new RoomConfig
            {
                RoomId = RoomId,
                Remark = Remark,
                Quality = Quality,
                OutputFormat = OutputFormat,
                SegmentEnabled = SegmentEnabled,
                SegmentMode = SegmentMode,
                SegmentMinutes = SegmentMinutes,
                SegmentSizeMb = SegmentSizeMb,
                AutoRecordEnabled = AutoRecordEnabled,
                Platform = Platform
            };
        }
    }

    [DataContract]
    public sealed class AppConfig
    {
        [DataMember(Order = 1)]
        public string OutputDirectory { get; set; }

        [DataMember(Order = 2)]
        public int CheckIntervalSeconds { get; set; }

        [DataMember(Order = 3)]
        public string DefaultQuality { get; set; }

        [DataMember(Order = 4)]
        public string DefaultFormat { get; set; }

        [DataMember(Order = 5)]
        public ObservableCollection<RoomConfig> Rooms { get; set; }

        [DataMember(Order = 6)]
        public string BilibiliCookieData { get; set; }

        [DataMember(Order = 7)]
        public string BilibiliUserName { get; set; }

        [DataMember(Order = 8)]
        public string DefaultPlatform { get; set; }

        [DataMember(Order = 9)]
        public string MediaOutputDirectory { get; set; }

        [DataMember(Order = 10)]
        public string MediaCookieBrowser { get; set; }

        [DataMember(Order = 11)]
        public string MediaProxy { get; set; }

        [DataMember(Order = 12)]
        public bool QueueExplicitlyCleared { get; set; }

        public static AppConfig CreateDefault()
        {
            return new AppConfig
            {
                OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                CheckIntervalSeconds = 16,
                DefaultQuality = "自动",
                DefaultFormat = "MP4",
                DefaultPlatform = "抖音",
                MediaOutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                MediaCookieBrowser = "不使用浏览器登录",
                Rooms = new ObservableCollection<RoomConfig>()
            };
        }
    }
}
