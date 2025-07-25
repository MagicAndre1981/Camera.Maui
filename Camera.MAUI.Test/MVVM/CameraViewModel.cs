﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ZXing;
using System.Windows.Markup;
using System.Collections.Specialized;
using Camera.MAUI.ZXingHelper;
using CommunityToolkit.Maui.Views;

namespace Camera.MAUI.Test;

public class CameraViewModel : INotifyPropertyChanged
{
    private CameraInfo camera = null;
    public CameraInfo Camera 
    {
        get => camera;
        set
        {
            camera = value;
            OnPropertyChanged(nameof(Camera));
            AutoStartPreview = false;
            OnPropertyChanged(nameof(AutoStartPreview));
            AutoStartPreview = true;
            OnPropertyChanged(nameof(AutoStartPreview));
        }
    }
    private ObservableCollection<CameraInfo> cameras = new();
    public ObservableCollection<CameraInfo> Cameras
    {
        get => cameras;
        set
        {
            cameras = value;
            OnPropertyChanged(nameof(Cameras));
        }
    }
    public int NumCameras
    {
        get => Cameras.Count; 
        set
        {
            if (value > 0)
                Camera = Cameras.First();
        }
    }
    private MicrophoneInfo micro = null;
    public MicrophoneInfo Microphone
    {
        get => micro;
        set
        {
            micro = value;
            OnPropertyChanged(nameof(Microphone));
        }
    }
    private ObservableCollection<MicrophoneInfo> micros = new();
    public ObservableCollection<MicrophoneInfo> Microphones
    {
        get => micros;
        set
        {
            micros = value;
            OnPropertyChanged(nameof(Microphones));
        }
    }
    public int NumMicrophones
    {
        get => Microphones.Count;
        set
        {
            if (value > 0)
                Microphone = Microphones.First();
        }
    }
    public MediaSource VideoSource { get; set; }
    public BarcodeDecodeOptions BarCodeOptions { get; set; }
    public string BarcodeText { get; set; } = "No barcode detected";
    public bool AutoStartPreview { get; set; } = false;
    public bool AutoStartRecording { get; set; } = false;
    private Result[] barCodeResults;
    public Result[] BarCodeResults 
    {
        get => barCodeResults;
        set
        {
            barCodeResults = value;
            if (barCodeResults != null && barCodeResults.Length > 0)
                BarcodeText = barCodeResults[0].Text;
            else
                BarcodeText = "No barcode detected";
            OnPropertyChanged(nameof(BarcodeText));
        }
    }
    private bool takeSnapshot = false;
    public bool TakeSnapshot 
    { 
        get => takeSnapshot;
        set
        {
            takeSnapshot = value;
            OnPropertyChanged(nameof(TakeSnapshot));
        } 
    }
    public float SnapshotSeconds { get; set; } = 0f;
    public string Seconds
    {
        get => SnapshotSeconds.ToString();
        set
        {
            if (float.TryParse(value, out float seconds))
            {
                SnapshotSeconds = seconds;
                OnPropertyChanged(nameof(SnapshotSeconds));
            }
        }
    }
    public Command StartCamera { get; set; }
    public Command StopCamera { get; set; }
    public Command TakeSnapshotCmd { get; set; }
    public string RecordingFile { get; set; }
    public Command StartRecording { get; set; }
    public Command StopRecording { get; set; }

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    public CameraViewModel()
    {
        BarCodeOptions = new BarcodeDecodeOptions
        {
            AutoRotate = true,
            PossibleFormats = { BarcodeFormat.EAN_13, BarcodeFormat.QR_CODE },
            ReadMultipleCodes = false,
            TryHarder = true,
            TryInverted = true
        };
        OnPropertyChanged(nameof(BarCodeOptions));
        StartCamera = new Command(() =>
        {
            AutoStartPreview = true;
            OnPropertyChanged(nameof(AutoStartPreview));
        });
        StopCamera = new Command(() =>
        {
            AutoStartPreview = false;
            OnPropertyChanged(nameof(AutoStartPreview));
        });
        TakeSnapshotCmd = new Command(() =>
        {
            TakeSnapshot = false;
            TakeSnapshot = true;
        });
#if IOS
        RecordingFile = Path.Combine(FileSystem.Current.CacheDirectory, "Video.mov");
#else
        RecordingFile = Path.Combine(FileSystem.Current.CacheDirectory, "Video.mp4");
#endif
        OnPropertyChanged(nameof(RecordingFile));
        StartRecording = new Command(() =>
        {
            AutoStartRecording = true;
            OnPropertyChanged(nameof(AutoStartRecording));
        });
        StopRecording = new Command(() =>
        {
            AutoStartRecording = false;
            OnPropertyChanged(nameof(AutoStartRecording));
            VideoSource = MediaSource.FromFile(RecordingFile);
            OnPropertyChanged(nameof(VideoSource));
        });
        OnPropertyChanged(nameof(StartCamera));
        OnPropertyChanged(nameof(StopCamera));
        OnPropertyChanged(nameof(TakeSnapshotCmd));
        OnPropertyChanged(nameof(StartRecording));
        OnPropertyChanged(nameof(StopRecording));
    }
}
