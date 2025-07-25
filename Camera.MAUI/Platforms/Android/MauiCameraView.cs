﻿using Android.Content;
using Android.Widget;
using Java.Util.Concurrent;
using Android.Graphics;
using CameraCharacteristics = Android.Hardware.Camera2.CameraCharacteristics;
using Android.Hardware.Camera2;
using Android.Media;
using Android.Views;
using Android.Util;
using Android.Hardware.Camera2.Params;
using Size = Android.Util.Size;
using Class = Java.Lang.Class;
using Rect = Android.Graphics.Rect;
using SizeF = Android.Util.SizeF;
using Android.Runtime;
using Android.OS;
using Android.Renderscripts;
using RectF = Android.Graphics.RectF;
using Android.Content.Res;

namespace Camera.MAUI.Platforms.Android;

internal class MauiCameraView : GridLayout
{
    private readonly CameraView cameraView;
    private IExecutorService executorService;
    private bool started = false;
    private int frames = 0;
    private bool initiated = false;
    private bool snapping = false;
    private bool recording = false;
    private readonly Context context;

    private readonly TextureView textureView;
    public CameraCaptureSession previewSession;
    public MediaRecorder mediaRecorder;
    private CaptureRequest.Builder previewBuilder;
    private CameraDevice cameraDevice;
    private readonly MyCameraStateCallback stateListener;
    private Size videoSize;
    private CameraManager cameraManager;
    private AudioManager audioManager;
    private readonly System.Timers.Timer timer;
    private readonly SparseIntArray ORIENTATIONS = new();
    private readonly SparseIntArray ORIENTATIONSFRONT = new();
    private CameraCharacteristics camChars;
    private PreviewCaptureStateCallback sessionCallback;
    private byte[] capturePhoto = null;
    private bool captureDone = false;
    private readonly ImageAvailableListener photoListener;
    private HandlerThread backgroundThread;
    private Handler backgroundHandler;
    private ImageReader imgReader;


    public MauiCameraView(Context context, CameraView cameraView) : base(context)
    {
        this.context = context;
        this.cameraView = cameraView;

        textureView = new(context);
        timer = new(33.3);
        timer.Elapsed += Timer_Elapsed;
        stateListener = new MyCameraStateCallback(this);
        photoListener = new ImageAvailableListener(this);
        AddView(textureView);
        ORIENTATIONS.Append((int)SurfaceOrientation.Rotation0, 90);
        ORIENTATIONS.Append((int)SurfaceOrientation.Rotation90, 0);
        ORIENTATIONS.Append((int)SurfaceOrientation.Rotation180, 270);
        ORIENTATIONS.Append((int)SurfaceOrientation.Rotation270, 180);

        ORIENTATIONSFRONT.Append((int)SurfaceOrientation.Rotation0, 270);
        ORIENTATIONSFRONT.Append((int)SurfaceOrientation.Rotation90, 0);
        ORIENTATIONSFRONT.Append((int)SurfaceOrientation.Rotation180, 90);
        ORIENTATIONSFRONT.Append((int)SurfaceOrientation.Rotation270, 180);

        InitDevices();
    }

    private void InitDevices()
    {
        if (!initiated && cameraView != null)
        {
            cameraManager = (CameraManager)context.GetSystemService(Context.CameraService);
            audioManager = (AudioManager)context.GetSystemService(Context.AudioService);
            cameraView.Cameras.Clear();
            foreach (var id in cameraManager.GetCameraIdList())
            {
                var cameraInfo = new CameraInfo { DeviceId = id, MinZoomFactor = 1 };
                var chars = cameraManager.GetCameraCharacteristics(id);
                if ((int)(chars.Get(CameraCharacteristics.LensFacing) as Java.Lang.Number) == (int)LensFacing.Back)
                {
                    cameraInfo.Name = "Back Camera";
                    cameraInfo.Position = CameraPosition.Back;
                }
                else if ((int)(chars.Get(CameraCharacteristics.LensFacing) as Java.Lang.Number) == (int)LensFacing.Front)
                {
                    cameraInfo.Name = "Front Camera";
                    cameraInfo.Position = CameraPosition.Front;
                }
                else
                {
                    cameraInfo.Name = "Camera " + id;
                    cameraInfo.Position = CameraPosition.Unknow;
                }
                cameraInfo.MaxZoomFactor = (float)(chars.Get(CameraCharacteristics.ScalerAvailableMaxDigitalZoom) as Java.Lang.Number);
                cameraInfo.HasFlashUnit = (bool)(chars.Get(CameraCharacteristics.FlashInfoAvailable) as Java.Lang.Boolean);
                cameraInfo.AvailableResolutions = new();
                try
                {
                    float[] maxFocus = (float[])chars.Get(CameraCharacteristics.LensInfoAvailableFocalLengths);
                    SizeF size = (SizeF)chars.Get(CameraCharacteristics.SensorInfoPhysicalSize);
                    cameraInfo.HorizontalViewAngle = (float)(2 * Math.Atan(size.Width / (maxFocus[0] * 2)));
                    cameraInfo.VerticalViewAngle = (float)(2 * Math.Atan(size.Height / (maxFocus[0] * 2)));
                }
                catch { }
                try
                {
                    StreamConfigurationMap map = (StreamConfigurationMap)chars.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
                    foreach (var s in map.GetOutputSizes(Class.FromType(typeof(ImageReader))))
                        cameraInfo.AvailableResolutions.Add(new(s.Width, s.Height));
                }
                catch
                {
                    if (cameraInfo.Position == CameraPosition.Back)
                        cameraInfo.AvailableResolutions.Add(new(1920, 1080));
                    cameraInfo.AvailableResolutions.Add(new(1280, 720));
                    cameraInfo.AvailableResolutions.Add(new(640, 480));
                    cameraInfo.AvailableResolutions.Add(new(352, 288));
                }
                cameraView.Cameras.Add(cameraInfo);
            }
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                cameraView.Microphones.Clear();
                foreach (var device in audioManager.Microphones)
                {
                    cameraView.Microphones.Add(new MicrophoneInfo { Name = "Microphone " + device.Type.ToString() + " " + device.Address, DeviceId = device.Id.ToString() });
                }
            }
            //Microphone = Micros.FirstOrDefault();
            executorService = Executors.NewSingleThreadExecutor();

            initiated = true;
            cameraView.RefreshDevices();
        }
    }

    internal async Task<CameraResult> StartRecordingAsync(string file, Microsoft.Maui.Graphics.Size Resolution, int frameRate, int bitRate, bool withAudio)
    {
        var result = CameraResult.Success;
        if (initiated && !recording)
        {
            if (await CameraView.RequestPermissions(true, true))
            {
                if (started) StopCamera();
                if (cameraView.Camera != null)
                {
                    try
                    {
                        camChars = cameraManager.GetCameraCharacteristics(cameraView.Camera.DeviceId);

                        StreamConfigurationMap map = (StreamConfigurationMap)camChars.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
                        videoSize = ChooseVideoSize(map.GetOutputSizes(Class.FromType(typeof(ImageReader))));
                        recording = true;

                        if (File.Exists(file))
                        {
                            File.Delete(file);
                        }

                        if (OperatingSystem.IsAndroidVersionAtLeast(31))
                            mediaRecorder = new MediaRecorder(context);
                        else
                            mediaRecorder = new MediaRecorder();
                        if (withAudio)
                        {
                            audioManager.Mode = Mode.Normal;
                            mediaRecorder.SetAudioSource(AudioSource.Mic);
                        }
                        mediaRecorder.SetVideoSource(VideoSource.Surface);
                        var ext = System.IO.Path.GetExtension(file).ToLower();
                        switch (ext)
                        {
                            case ".3gp":
                                mediaRecorder.SetOutputFormat(OutputFormat.ThreeGpp);
                                mediaRecorder.SetVideoEncoder(VideoEncoder.H264);
                                if (withAudio)
                                    mediaRecorder.SetAudioEncoder(AudioEncoder.Aac);
                                break;
                            case ".mp4":
                                mediaRecorder.SetOutputFormat(OutputFormat.Mpeg4);
                                mediaRecorder.SetVideoEncoder(VideoEncoder.H264);
                                if (withAudio)
                                    mediaRecorder.SetAudioEncoder(AudioEncoder.Aac);
                                break;
                            case ".webm":
                                mediaRecorder.SetOutputFormat(OutputFormat.Webm);
                                mediaRecorder.SetVideoEncoder(VideoEncoder.Vp8);
                                if (withAudio)
                                    mediaRecorder.SetAudioEncoder(AudioEncoder.Vorbis);
                                break;
                            default:
                                throw new ArgumentException($"Invalid file extension '{ext}'! Please use '.3gp', '.mp4' or '.webm'!");
                        }
                        mediaRecorder.SetOutputFile(file);
                        mediaRecorder.SetVideoEncodingBitRate(bitRate);
                        mediaRecorder.SetVideoFrameRate(frameRate);

                        var maxVideoSize = ChooseMaxVideoSize(map.GetOutputSizes(Class.FromType(typeof(ImageReader))));
                        if (Resolution.Width != 0 && Resolution.Height != 0)
                            maxVideoSize = new((int)Resolution.Width, (int)Resolution.Height);
                        mediaRecorder.SetVideoSize(maxVideoSize.Width, maxVideoSize.Height);

                        IWindowManager windowManager = context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();

                        int rotation = (int)windowManager.DefaultDisplay.Rotation;
                        int orientation = 0;

                        // Set camera rotation based on front or back camera.
                        if (cameraView.Camera.Position == CameraPosition.Back)
                            orientation = ORIENTATIONS.Get(rotation);
                        else
                            orientation = ORIENTATIONSFRONT.Get(rotation);

                        mediaRecorder.SetOrientationHint(orientation);

                        Console.WriteLine($"Rotation: {rotation} Orientation: {orientation}");


                        mediaRecorder.Prepare();

                        if (OperatingSystem.IsAndroidVersionAtLeast(28))
                            cameraManager.OpenCamera(cameraView.Camera.DeviceId, executorService, stateListener);
                        else
                            cameraManager.OpenCamera(cameraView.Camera.DeviceId, stateListener, null);
                        started = true;
                    }
                    catch (Exception ex)
                    {
                        Console.Write(ex.ToString());
                        Console.Write(ex.StackTrace);
                        result = CameraResult.AccessError;
                    }
                }
                else
                    result = CameraResult.NoCameraSelected;
            }
            else
                result = CameraResult.AccessDenied;
        }
        else
            result = CameraResult.NotInitiated;

        return result;
    }

    private async void StartPreview()
    {
        while (textureView.SurfaceTexture == null)
        {
            await Task.Delay(100);
        }
        SurfaceTexture texture = textureView.SurfaceTexture;
        texture.SetDefaultBufferSize(videoSize.Width, videoSize.Height);

        previewBuilder = cameraDevice.CreateCaptureRequest(recording ? CameraTemplate.Record : CameraTemplate.Preview);
        var surfaces = new List<OutputConfiguration>();
        var surfaces26 = new List<Surface>();
        var previewSurface = new Surface(texture);
        surfaces.Add(new OutputConfiguration(previewSurface));
        surfaces26.Add(previewSurface);
        previewBuilder.AddTarget(previewSurface);
        if (imgReader != null)
        {
            surfaces.Add(new OutputConfiguration(imgReader.Surface));
            surfaces26.Add(imgReader.Surface);
        }
        if (mediaRecorder != null)
        {
            surfaces.Add(new OutputConfiguration(mediaRecorder.Surface));
            surfaces26.Add(mediaRecorder.Surface);
            previewBuilder.AddTarget(mediaRecorder.Surface);
        }

        sessionCallback = new PreviewCaptureStateCallback(this);
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            SessionConfiguration config = new((int)SessionType.Regular, surfaces, executorService, sessionCallback);
            cameraDevice.CreateCaptureSession(config);
        }
        else
        {
#pragma warning disable CS0618 // El tipo o el miembro están obsoletos
            cameraDevice.CreateCaptureSession(surfaces26, sessionCallback, null);
#pragma warning restore CS0618 // El tipo o el miembro están obsoletos
        }
    }
    private void UpdatePreview()
    {
        if (null == cameraDevice)
            return;

        try
        {
            previewBuilder.Set(CaptureRequest.ControlMode, Java.Lang.Integer.ValueOf((int)ControlMode.Auto));
            //Rect m = (Rect)camChars.Get(CameraCharacteristics.SensorInfoActiveArraySize);
            //videoSize = new Size(m.Width(), m.Height());
            //AdjustAspectRatio(videoSize.Width, videoSize.Height);
            AdjustAspectRatio(videoSize.Width, videoSize.Height);
            SetZoomFactor(cameraView.ZoomFactor);
            //previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
            if (recording)
                mediaRecorder?.Start();
        }
        catch (CameraAccessException e)
        {
            e.PrintStackTrace();
        }
    }
    internal async Task<CameraResult> StartCameraAsync(Microsoft.Maui.Graphics.Size PhotosResolution)
    {
        var result = CameraResult.Success;
        if (initiated)
        {
            if (await CameraView.RequestPermissions())
            {
                if (started) StopCamera();
                if (cameraView.Camera != null)
                {
                    try
                    {
                        camChars = cameraManager.GetCameraCharacteristics(cameraView.Camera.DeviceId);

                        StreamConfigurationMap map = (StreamConfigurationMap)camChars.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
                        videoSize = ChooseVideoSize(map.GetOutputSizes(Class.FromType(typeof(ImageReader))));
                        var maxVideoSize = ChooseMaxVideoSize(map.GetOutputSizes(Class.FromType(typeof(ImageReader))));
                        if (PhotosResolution.Width != 0 && PhotosResolution.Height != 0)
                            maxVideoSize = new((int)PhotosResolution.Width, (int)PhotosResolution.Height);
                        imgReader = ImageReader.NewInstance(maxVideoSize.Width, maxVideoSize.Height, ImageFormatType.Jpeg, 1);
                        backgroundThread = new HandlerThread("CameraBackground");
                        backgroundThread.Start();
                        backgroundHandler = new Handler(backgroundThread.Looper);
                        imgReader.SetOnImageAvailableListener(photoListener, backgroundHandler);

                        if (OperatingSystem.IsAndroidVersionAtLeast(28))
                            cameraManager.OpenCamera(cameraView.Camera.DeviceId, executorService, stateListener);
                        else
                            cameraManager.OpenCamera(cameraView.Camera.DeviceId, stateListener, null);
                        timer.Start();

                        started = true;
                    }
                    catch
                    {
                        result = CameraResult.AccessError;
                    }
                }
                else
                    result = CameraResult.NoCameraSelected;
            }
            else
                result = CameraResult.AccessDenied;
        }
        else
            result = CameraResult.NotInitiated;

        return result;
    }
    internal Task<CameraResult> StopRecordingAsync()
    {
        recording = false;
        return StartCameraAsync(cameraView.PhotosResolution);
    }

    internal CameraResult StopCamera()
    {
        CameraResult result = CameraResult.Success;
        if (initiated)
        {
            timer.Stop();
            try
            {
                mediaRecorder?.Stop();
                mediaRecorder?.Dispose();
            }
            catch { }
            try
            {
                backgroundThread?.QuitSafely();
                backgroundThread?.Join();
                backgroundThread = null;
                backgroundHandler = null;
                imgReader?.Dispose();
                imgReader = null;
            }
            catch { }
            try
            {
                previewSession?.StopRepeating();
                previewSession?.Dispose();
            }
            catch { }
            try
            {
                cameraDevice?.Close();
                cameraDevice?.Dispose();
            }
            catch { }
            previewSession = null;
            cameraDevice = null;
            previewBuilder = null;
            mediaRecorder = null;
            started = false;
            recording = false;
        }
        else
            result = CameraResult.NotInitiated;
        return result;
    }
    internal void DisposeControl()
    {
        try
        {
            if (started) StopCamera();
            executorService?.Shutdown();
            executorService?.Dispose();
            RemoveAllViews();
            textureView?.Dispose();
            timer?.Dispose();
            Dispose();
        }
        catch { }
    }
    private void ProccessQR()
    {
        Task.Run(() =>
        {
            Bitmap bitmap = TakeSnap();
            if (bitmap != null)
            {
                System.Diagnostics.Debug.WriteLine($"Processing QR ({bitmap.Width}x{bitmap.Height}) " + DateTime.Now.ToString("mm:ss:fff"));
                cameraView.DecodeBarcode(bitmap);
                bitmap.Dispose();
                System.Diagnostics.Debug.WriteLine("QR Processed " + DateTime.Now.ToString("mm:ss:fff"));
            }
            lock (cameraView.currentThreadsLocker) cameraView.currentThreads--;
        });
    }
    private void RefreshSnapShot()
    {
        cameraView.RefreshSnapshot(GetSnapShot(cameraView.AutoSnapShotFormat, true));
    }

    private void Timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
    {
        if (!snapping && cameraView != null && cameraView.AutoSnapShotSeconds > 0 && (DateTime.Now - cameraView.lastSnapshot).TotalSeconds >= cameraView.AutoSnapShotSeconds)
        {
            Task.Run(() => RefreshSnapShot());
        }
        else if (cameraView.BarCodeDetectionEnabled)
        {
            frames++;
            if (frames >= cameraView.BarCodeDetectionFrameRate)
            {
                bool processQR = false;
                lock (cameraView.currentThreadsLocker)
                {
                    if (cameraView.currentThreads < cameraView.BarCodeDetectionMaxThreads)
                    {
                        cameraView.currentThreads++;
                        processQR = true;
                    }
                }
                if (processQR)
                {
                    ProccessQR();
                    frames = 0;
                }
            }
        }

    }

    private Bitmap TakeSnap()
    {
        Bitmap bitmap = null;
        try
        {
            MainThread.InvokeOnMainThreadAsync(() => { bitmap = textureView.GetBitmap(null); bitmap = textureView.Bitmap; }).Wait();
            if (bitmap != null)
            {
                int oriWidth = bitmap.Width;
                int oriHeight = bitmap.Height;

                bitmap = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, textureView.GetTransform(null), false);
                float xscale = (float)oriWidth / bitmap.Width;
                float yscale = (float)oriHeight / bitmap.Height;
                bitmap = Bitmap.CreateBitmap(bitmap, (bitmap.Width - Width) / 2, (bitmap.Height - Height) / 2, Width, Height);
                if (textureView.ScaleX == -1)
                {
                    Matrix matrix = new();
                    matrix.PreScale(-1, 1);
                    bitmap = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, false);
                }
            }
        }
        catch { }
        return bitmap;
    }
    internal async Task<System.IO.Stream> TakePhotoAsync(ImageFormat imageFormat)
    {
        MemoryStream stream = null;
        if (started && !recording)
        {
            CaptureRequest.Builder singleRequest = cameraDevice.CreateCaptureRequest(CameraTemplate.StillCapture);
            captureDone = false;
            capturePhoto = null;
            if (cameraView.Camera.HasFlashUnit)
            {
                switch (cameraView.FlashMode)
                {
                    case FlashMode.Auto:
                        singleRequest.Set(CaptureRequest.FlashMode, (int)ControlAEMode.OnAutoFlash);
                        break;
                    case FlashMode.Enabled:
                        singleRequest.Set(CaptureRequest.FlashMode, (int)ControlAEMode.On);
                        break;
                    case FlashMode.Disabled:
                        singleRequest.Set(CaptureRequest.FlashMode, (int)ControlAEMode.Off);
                        break;
                }
            }
            int rotation = GetJpegOrientation();
            singleRequest.Set(CaptureRequest.JpegOrientation, rotation);

            var destZoom = Math.Clamp(cameraView.ZoomFactor, 1, Math.Min(6, cameraView.Camera.MaxZoomFactor)) - 1;
            Rect m = (Rect)camChars.Get(CameraCharacteristics.SensorInfoActiveArraySize);
            int minW = (int)(m.Width() / (cameraView.Camera.MaxZoomFactor));
            int minH = (int)(m.Height() / (cameraView.Camera.MaxZoomFactor));
            int newWidth = (int)(m.Width() - (minW * destZoom));
            int newHeight = (int)(m.Height() - (minH * destZoom));
            Rect zoomArea = new((m.Width() - newWidth) / 2, (m.Height() - newHeight) / 2, newWidth, newHeight);
            singleRequest.Set(CaptureRequest.ScalerCropRegion, zoomArea);

            singleRequest.AddTarget(imgReader.Surface);
            try
            {
                previewSession.Capture(singleRequest.Build(), null, null);
                while (!captureDone) await Task.Delay(50);
                if (capturePhoto != null)
                {
                    if (textureView.ScaleX == -1 || imageFormat != ImageFormat.JPEG)
                    {
                        Bitmap bitmap = BitmapFactory.DecodeByteArray(capturePhoto, 0, capturePhoto.Length);
                        if (bitmap != null)
                        {
                            if (textureView.ScaleX == -1)
                            {
                                Matrix matrix = new();
                                matrix.PreRotate(rotation);
                                matrix.PostScale(-1, 1);
                                bitmap = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, false);
                            }
                            var iformat = imageFormat switch
                            {
                                ImageFormat.JPEG => Bitmap.CompressFormat.Jpeg,
                                _ => Bitmap.CompressFormat.Png
                            };
                            stream = new();
                            bitmap.Compress(iformat, 100, stream);
                            stream.Position = 0;
                        }
                    }
                    else
                    {
                        stream = new();
                        stream.Write(capturePhoto);
                        stream.Position = 0;
                    }
                }
            }
            catch (Java.Lang.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
            }
        }
        return stream;
    }

    private MemoryStream ReadSnapShotStream(ImageFormat imageFormat)
    {
        MemoryStream stream = new();
        Bitmap bitmap = TakeSnap();

        if (bitmap != null)
        {
            var iformat = imageFormat switch
            {
                ImageFormat.JPEG => Bitmap.CompressFormat.Jpeg,
                _ => Bitmap.CompressFormat.Png
            };
            bitmap.Compress(iformat, 100, stream);
            stream.Position = 0;
            bitmap.Dispose();
        }
        return stream;
    }

    internal System.IO.Stream GetSnapShotStream(ImageFormat imageFormat)
    {
        MemoryStream stream = null;
        if (started && !snapping)
        {
            snapping = true;
            stream = ReadSnapShotStream(imageFormat);
            snapping = false;
        }
        return stream;
    }

    internal ImageSource GetSnapShot(ImageFormat imageFormat, bool auto = false)
    {
        ImageSource result = null;

        if (started && !snapping)
        {
            snapping = true;
            var stream = ReadSnapShotStream(imageFormat);

            if (stream != null && stream.CanRead)
            {
                if (auto)
                {
                    if (cameraView.AutoSnapShotAsImageSource)
                        result = ImageSource.FromStream(() => stream);
                    cameraView.SnapShotStream?.Dispose();
                    cameraView.SnapShotStream = stream;
                }
                else
                    result = ImageSource.FromStream(() => stream);
            }
            snapping = false;
        }
        return result;
    }

    internal bool SaveSnapShot(ImageFormat imageFormat, string SnapFilePath)
    {
        bool result = true;

        if (started && !snapping)
        {
            snapping = true;
            Bitmap bitmap = TakeSnap();
            if (bitmap != null)
            {
                if (File.Exists(SnapFilePath)) File.Delete(SnapFilePath);
                var iformat = imageFormat switch
                {
                    ImageFormat.JPEG => Bitmap.CompressFormat.Jpeg,
                    _ => Bitmap.CompressFormat.Png
                };
                using FileStream stream = new(SnapFilePath, FileMode.OpenOrCreate);
                bitmap.Compress(iformat, 80, stream);
                stream.Close();
            }
            snapping = false;
        }
        else
            result = false;

        return result;
    }
    public void UpdateMirroredImage()
    {
        if (cameraView != null && textureView != null)
        {
            if (cameraView.MirroredImage)
                textureView.ScaleX = -1;
            else
                textureView.ScaleX = 1;
        }
    }
    internal void UpdateTorch()
    {
        if (cameraView.Camera != null && cameraView.Camera.HasFlashUnit)
        {
            if (started)
            {
                previewBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.On);
                previewBuilder.Set(CaptureRequest.FlashMode, cameraView.TorchEnabled ? (int)ControlAEMode.OnAutoFlash : (int)ControlAEMode.Off);
                previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
            }
            else if (initiated)
                cameraManager.SetTorchMode(cameraView.Camera.DeviceId, cameraView.TorchEnabled);
        }
    }
    internal void UpdateFlashMode()
    {
        if (previewSession != null && previewBuilder != null && cameraView.Camera != null && cameraView != null)
        {
            try
            {
                if (cameraView.Camera.HasFlashUnit)
                {
                    switch (cameraView.FlashMode)
                    {
                        case FlashMode.Auto:
                            previewBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.OnAutoFlash);
                            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
                            break;
                        case FlashMode.Enabled:
                            previewBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.On);
                            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
                            break;
                        case FlashMode.Disabled:
                            previewBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.Off);
                            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
                            break;
                    }
                }
            }
            catch (System.Exception)
            {
            }
        }
    }
    internal void SetZoomFactor(float zoom)
    {
        if (previewSession != null && previewBuilder != null && cameraView.Camera != null)
        {
            //if (OperatingSystem.IsAndroidVersionAtLeast(30))
            //{
            //previewBuilder.Set(CaptureRequest.ControlZoomRatio, Math.Max(Camera.MinZoomFactor, Math.Min(zoom, Camera.MaxZoomFactor)));
            //}
            var destZoom = Math.Clamp(zoom, 1, Math.Min(6, cameraView.Camera.MaxZoomFactor)) - 1;
            Rect m = (Rect)camChars.Get(CameraCharacteristics.SensorInfoActiveArraySize);
            int minW = (int)(m.Width() / (cameraView.Camera.MaxZoomFactor));
            int minH = (int)(m.Height() / (cameraView.Camera.MaxZoomFactor));
            int newWidth = (int)(m.Width() - (minW * destZoom));
            int newHeight = (int)(m.Height() - (minH * destZoom));
            Rect zoomArea = new((m.Width() - newWidth) / 2, (m.Height() - newHeight) / 2, newWidth, newHeight);
            previewBuilder.Set(CaptureRequest.ScalerCropRegion, zoomArea);
            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
        }
    }
    internal void ForceAutoFocus()
    {
        if (previewSession != null && previewBuilder != null && cameraView.Camera != null)
        {
            previewBuilder.Set(CaptureRequest.ControlAfMode, Java.Lang.Integer.ValueOf((int)ControlAFMode.Off));
            previewBuilder.Set(CaptureRequest.ControlAfTrigger, Java.Lang.Integer.ValueOf((int)ControlAFTrigger.Cancel));
            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);
            previewBuilder.Set(CaptureRequest.ControlAfMode, Java.Lang.Integer.ValueOf((int)ControlAFMode.Auto));
            previewBuilder.Set(CaptureRequest.ControlAfTrigger, Java.Lang.Integer.ValueOf((int)ControlAFTrigger.Start));
            previewSession.SetRepeatingRequest(previewBuilder.Build(), null, null);

        }
    }

    /// <summary>
    /// Choose the largest-possible video size from a list of choices
    /// (with an aspect ratio of 4:3).
    /// </summary>
    private static Size ChooseMaxVideoSize(Size[] choices)
    {
        Size result = choices[0];
        int maxArea = 0;

        foreach (Size size in choices)
        {
            if (size.Width == size.Height * 4 / 3 && size.Width * size.Height > maxArea)
            {
                result = size;
                maxArea = size.Width * size.Height;
            }
        }

        return result;
    }

    /// <summary>
    /// Choose the smallest-possible video size that is just bigger than the size of the CameraView
    /// (and has an aspect ration of 4:3).
    /// If that size of the CameraView is not known (yet), the maximum video size is chosen.
    /// </summary>
    private Size ChooseVideoSize(Size[] choices)
    {
        if (Width <= 0 || Height <= 0)
            return ChooseMaxVideoSize(choices);

        Size result = choices[0];
        int minArea = int.MaxValue;
        bool swapped = IsDimensionSwapped();
        foreach (Size size in choices)
        {
            int w = swapped ? size.Height : size.Width;
            int h = swapped ? size.Width : size.Height;
            if (size.Width == size.Height * 4 / 3 && w >= Width && h >= Height && size.Width * size.Height < minArea)
            {
                result = size;
                minArea = size.Width * size.Height;
            }
        }

        return result;
    }

    private void AdjustAspectRatio(int videoWidth, int videoHeight)
    {
        Matrix txform = new();
        /*
        float scaleX = (float)videoWidth / Width;
        float scaleY = (float)videoHeight / Height;
        bool swapped = IsDimensionSwapped();
        if (swapped)
        {
            scaleX = (float)videoHeight / Width;
            scaleY = (float)videoWidth / Height;
        }
        if (scaleX <= scaleY)
        {
            scaleY /= scaleX;
            scaleX = 1;
        }
        else
        {
            scaleX /= scaleY;
            scaleY = 1;
        }
        */
        RectF viewRect = new(0, 0, Width, Height);
        float centerX = viewRect.CenterX();
        float centerY = viewRect.CenterY();
        RectF bufferRect = new(0, 0, videoHeight, videoWidth);
        bufferRect.Offset(centerX - bufferRect.CenterX(), centerY - bufferRect.CenterY());
        txform.SetRectToRect(viewRect, bufferRect, Matrix.ScaleToFit.Fill);
        float scale = Math.Max(
                (float)Height / videoHeight,
                (float)Width / videoWidth);
        txform.PostScale(scale, scale, centerX, centerY);

        //txform.PostScale(scaleX, scaleY, centerX, centerY);
        IWindowManager windowManager = context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();
        var rotation = windowManager.DefaultDisplay.Rotation;
        if (SurfaceOrientation.Rotation90 == rotation || SurfaceOrientation.Rotation270 == rotation)
        {
            txform.PostRotate(90 * ((int)rotation - 2), centerX, centerY);
        }
        else if (SurfaceOrientation.Rotation180 == rotation)
        {
            txform.PostRotate(180, centerX, centerY);
        }
        textureView.SetTransform(txform);
    }

    protected override async void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        if (started && !recording)
            await StartCameraAsync(cameraView.PhotosResolution);
    }

    private bool IsDimensionSwapped()
    {
        IWindowManager windowManager = context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();
        var displayRotation = windowManager.DefaultDisplay.Rotation;
        var chars = cameraManager.GetCameraCharacteristics(cameraView.Camera.DeviceId);
        int sensorOrientation = (int)(chars.Get(CameraCharacteristics.SensorOrientation) as Java.Lang.Integer);
        bool swappedDimensions = false;
        switch (displayRotation)
        {
            case SurfaceOrientation.Rotation0:
            case SurfaceOrientation.Rotation180:
                if (sensorOrientation == 90 || sensorOrientation == 270)
                {
                    swappedDimensions = true;
                }
                break;
            case SurfaceOrientation.Rotation90:
            case SurfaceOrientation.Rotation270:
                if (sensorOrientation == 0 || sensorOrientation == 180)
                {
                    swappedDimensions = true;
                }
                break;
        }
        return swappedDimensions;
    }

    private int GetJpegOrientation()
    {
        IWindowManager windowManager = context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();
        var displayRotation = windowManager.DefaultDisplay.Rotation;
        var chars = cameraManager.GetCameraCharacteristics(cameraView.Camera.DeviceId);
        int sensorOrientation = (int)(chars.Get(CameraCharacteristics.SensorOrientation) as Java.Lang.Integer);
        int deviceOrientation = displayRotation switch
        {
            SurfaceOrientation.Rotation90 => 90,
            SurfaceOrientation.Rotation180 => 180,
            SurfaceOrientation.Rotation270 => 270,
            _ => 0
        };

        var cameraPosition = cameraView.Camera.Position == CameraPosition.Front ? -1 : 1;
        return (sensorOrientation - deviceOrientation * cameraPosition + 360) % 360;
    }
    private class MyCameraStateCallback : CameraDevice.StateCallback
    {
        private readonly MauiCameraView cameraView;
        public MyCameraStateCallback(MauiCameraView camView)
        {
            cameraView = camView;
        }
        public override void OnOpened(CameraDevice camera)
        {
            if (camera != null)
            {
                cameraView.cameraDevice = camera;
                cameraView.StartPreview();
            }
        }

        public override void OnDisconnected(CameraDevice camera)
        {
            camera.Close();
            cameraView.cameraDevice = null;
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            camera?.Close();
            cameraView.cameraDevice = null;
        }
    }

    private class PreviewCaptureStateCallback : CameraCaptureSession.StateCallback
    {
        private readonly MauiCameraView cameraView;
        public PreviewCaptureStateCallback(MauiCameraView camView)
        {
            cameraView = camView;
        }
        public override void OnConfigured(CameraCaptureSession session)
        {
            cameraView.previewSession = session;
            cameraView.UpdatePreview();

        }
        public override void OnConfigureFailed(CameraCaptureSession session)
        {
        }
    }
    class ImageAvailableListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        private readonly MauiCameraView cameraView;

        public ImageAvailableListener(MauiCameraView camView)
        {
            cameraView = camView;
        }
        public void OnImageAvailable(ImageReader reader)
        {
            try
            {
                var image = reader?.AcquireNextImage();
                if (image == null)
                    return;

                var buffer = image.GetPlanes()?[0].Buffer;
                if (buffer == null)
                    return;

                var imageData = new byte[buffer.Capacity()];
                buffer.Get(imageData);
                cameraView.capturePhoto = imageData;
                buffer.Clear();
                image.Close();
            }
            catch
            {
            }
            cameraView.captureDone = true;
        }
    }
}


