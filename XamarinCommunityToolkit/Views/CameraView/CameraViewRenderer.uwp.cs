﻿using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Xamarin.CommunityToolkit.UI.Views;
using Xamarin.Forms;
using Xamarin.Forms.Platform.UWP;
using MediaSource = Xamarin.Forms.MediaSource;

[assembly: ExportRenderer(typeof(CameraView), typeof(CameraViewRenderer))]
namespace Xamarin.CommunityToolkit.UI.Views
{
	public class CameraViewRenderer : ViewRenderer<CameraView, CaptureElement>
	{
		MediaCapture mediaCapture;
		bool isPreviewing;
		Lamp flash;
		LowLagMediaRecording mediaRecording;
		string filePath;
		bool busy;
		VideoStabilizationEffect videoStabilizationEffect;
		MediaEncodingProfile encodingProfile;
		VideoEncodingProperties inputPropertiesBackup;
		VideoEncodingProperties outputPropertiesBackup;

		public CameraViewRenderer()
		{
			encodingProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
		}

		bool IsBusy
		{
			get => busy;
			set
			{
				if (busy != value)
					Element.IsBusy = busy = value;
			}
		}

		bool Available
		{
			get => Element?.IsAvailable ?? false;
			set
			{
				if (Element != null && Element.IsAvailable != value)
					Element.IsAvailable = value;
			}
		}

		protected override async void OnElementChanged(ElementChangedEventArgs<CameraView> e)
		{
			Available = false;
			base.OnElementChanged(e);
			if (e.OldElement != null)
			{
				e.OldElement.ShutterClicked += HandleShutter;
			}
			if (e.NewElement != null)
			{
				if (Control != null)
				{
					await CleanupCameraAsync();
					mediaCapture.Failed -= MediaCaptureFailed;
				}

				SetNativeControl(new CaptureElement());
				Control.HorizontalAlignment = HorizontalAlignment.Stretch;
				Control.VerticalAlignment = VerticalAlignment.Stretch;

				e.NewElement.ShutterClicked += HandleShutter;

				isPreviewing = false;
				await InitializeCameraAsync();

				if (mediaCapture != null)
					mediaCapture.Failed += MediaCaptureFailed;
			}
		}

		async void HandleShutter(object sender, EventArgs e)
		{
			if (IsBusy)
				return;

			IsBusy = true;
			switch (Element.CaptureOptions)
			{
				default:
				case CameraCaptureOptions.Default:
				case CameraCaptureOptions.Photo:
					if (mediaRecording != null)
						await HandleVideo();
					else
						Element.RaiseMediaCaptured(new MediaCapturedEventArgs(image: await GetImage()));
					break;
				case CameraCaptureOptions.Video:
					await HandleVideo();
					break;
			}
			IsBusy = false;

			async Task HandleVideo()
			{
				if (mediaRecording == null)
					await StartRecord();
				else
					Element.RaiseMediaCaptured(new MediaCapturedEventArgs(video: await StopRecord()));
			}
		}

		async Task<ImageSource> GetImage()
		{
			IsBusy = true;
			var imageProp = ImageEncodingProperties.CreateUncompressed(MediaPixelFormat.Bgra8);
			var lowLagCapture = await mediaCapture.PrepareLowLagPhotoCaptureAsync(imageProp);
			var capturedPhoto = await lowLagCapture.CaptureAsync();

			await lowLagCapture.FinishAsync();
			if (Element.SavePhotoToFile)
			{
				//TODO replace platform specifics
				//var localFolder = Element.OnThisPlatform().GetPhotoFolder();
				var localFolder = "PhotoFolder";
				var destinationFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(localFolder, CreationCollisionOption.OpenIfExists);
				var file = await destinationFolder.CreateFileAsync($"{DateTime.Now.ToString("yyyyddMM_HHmmss")}.jpg", CreationCollisionOption.GenerateUniqueName);
				using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
				{
					var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
					encoder.SetSoftwareBitmap(capturedPhoto.Frame.SoftwareBitmap);
					await encoder.FlushAsync();
				}
			}

			// Encode an output stream, it seems you can't use the UWP Frame stream directly
			var outputStream = new InMemoryRandomAccessStream();
			var outputEncoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, outputStream);
			outputEncoder.SetSoftwareBitmap(capturedPhoto.Frame.SoftwareBitmap);
			await outputEncoder.FlushAsync();

			IsBusy = false;
			return ImageSource.FromStream(() => outputStream.AsStream());
		}

		async Task StartRecord()
		{
			//TODO replace platform specifics
			//var localFolder = Element.On<PlatformConfiguration.Windows>().GetVideoFolder();
			var localFolder = "Video";
			var destinationFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(localFolder, CreationCollisionOption.OpenIfExists);
			var file = await destinationFolder.CreateFileAsync($"{DateTime.Now.ToString("yyyyddMM_HHmmss")}.mp4", CreationCollisionOption.GenerateUniqueName);
			filePath = file.Path;
			if (Element.VideoStabilization)
			{
				var stabilizerDefinition = new VideoStabilizationEffectDefinition();
				videoStabilizationEffect = (VideoStabilizationEffect)await mediaCapture.AddVideoEffectAsync(stabilizerDefinition, MediaStreamType.VideoRecord);
				var recommendation = videoStabilizationEffect.GetRecommendedStreamConfiguration(mediaCapture.VideoDeviceController, encodingProfile.Video);
				if (recommendation.InputProperties != null)
				{
					inputPropertiesBackup = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoRecord) as VideoEncodingProperties;
					await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoRecord, recommendation.InputProperties);
					await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, recommendation.InputProperties);
				}
				if (recommendation.OutputProperties != null)
				{
					outputPropertiesBackup = encodingProfile.Video;
					encodingProfile.Video = recommendation.OutputProperties;
				}
				videoStabilizationEffect.Enabled = true;
			}
			mediaRecording = await mediaCapture.PrepareLowLagRecordToStorageFileAsync(encodingProfile, file);
			await mediaRecording.StartAsync();
		}

		async Task<MediaSource> StopRecord()
		{
			if (mediaRecording == null)
				return null;

			await mediaRecording.StopAsync();
			await mediaRecording.FinishAsync();
			mediaRecording = null;

			if (videoStabilizationEffect != null)
			{
				await mediaCapture.RemoveEffectAsync(videoStabilizationEffect);
				videoStabilizationEffect = null;

				if (inputPropertiesBackup != null)
				{
					await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoRecord, inputPropertiesBackup);
					inputPropertiesBackup = null;
				}
				if (outputPropertiesBackup != null)
				{
					encodingProfile.Video = outputPropertiesBackup;
					outputPropertiesBackup = null;
				}
				videoStabilizationEffect = null;
			}

			return MediaSource.FromFile(filePath);
		}

		void MediaCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
			=> Element?.RaiseMediaCaptureFailed(errorEventArgs.Message);

		protected override async void OnElementPropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(CameraView.CameraOptions):
					await CleanupCameraAsync();
					await InitializeCameraAsync();
					break;
				case nameof(CameraView.FlashMode):
					if (flash != null)
						flash.IsEnabled = Element.FlashMode == CameraFlashMode.Torch || Element.FlashMode == CameraFlashMode.On;
					break;
				// Only supported by Android, removed until we have platform specifics
				//case nameof(CameraView.PreviewAspect):
				//	// TODO
				//	break;
				case nameof(CameraView.Zoom):
					UpdateZoom();
					break;
			}
			base.OnElementPropertyChanged(sender, e);
		}

		void UpdateZoom()
		{
			var zoomControl = mediaCapture.VideoDeviceController.ZoomControl;
			if (!zoomControl.Supported)
				return;

			var settings = new ZoomSettings
			{
				//TODO replace clamp
				Value = Clamp(Element.Zoom, zoomControl.Min, zoomControl.Max),
				Mode = zoomControl.SupportedModes.Contains(ZoomTransitionMode.Smooth)
					? ZoomTransitionMode.Smooth
					: zoomControl.SupportedModes.First()
			};

			zoomControl.Configure(settings);

			// Added here since it's an internal method to XF
			static float Clamp(double value, float min, float max)
			{
				if (value.CompareTo(min) < 0)
					return min;
				if (value.CompareTo(max) > 0)
					return max;
				return (float)value;
			}
		}

		DeviceInformation FilterCamera(DeviceInformationCollection cameraDevices, Windows.Devices.Enumeration.Panel panel)
		{
			foreach (var cam in cameraDevices)
			{
				if (cam.EnclosureLocation?.Panel == panel)
					return cam;
			}
			return null;
		}

		async Task InitializeCameraAsync()
		{
			Available = false;
			if (mediaCapture != null)
				return;

			var cameraDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
			if (cameraDevices.Count == 0)
			{
				Element?.RaiseMediaCaptureFailed("Camera devices not found.");
				return;
			}

			IsBusy = true;
			DeviceInformation device = null;
			switch (Element.CameraOptions)
			{
				default:
				case CameraOptions.Default:
					device = cameraDevices[0];
					break;
				case CameraOptions.Front:
					device = FilterCamera(cameraDevices, Windows.Devices.Enumeration.Panel.Front);
					break;
				case CameraOptions.Back:
					device = FilterCamera(cameraDevices, Windows.Devices.Enumeration.Panel.Back);
					break;
				case CameraOptions.External:
					device = FilterCamera(cameraDevices, Windows.Devices.Enumeration.Panel.Unknown);
					break;
			}
			if (device == null)
			{
				Element?.RaiseMediaCaptureFailed($"{Element.CameraOptions} camera not found.");
				IsBusy = false;
				return;
			}

			var audioDevice = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
			var selectedAudioDevice = audioDevice.Count == 0 ? null : audioDevice[0].Id;

			mediaCapture = new MediaCapture();
			try
			{
				await mediaCapture.InitializeAsync(new MediaCaptureInitializationSettings
				{
					VideoDeviceId = device.Id,
					MediaCategory = MediaCategory.Media,
					StreamingCaptureMode = selectedAudioDevice == null ? StreamingCaptureMode.Video : StreamingCaptureMode.AudioAndVideo,
					AudioProcessing = Windows.Media.AudioProcessing.Default,
					AudioDeviceId = selectedAudioDevice
				});
				flash = await Lamp.GetDefaultAsync();
				if (mediaCapture?.VideoDeviceController.ZoomControl.Supported ?? false)
					Element.MaxZoom = mediaCapture.VideoDeviceController.ZoomControl.Max;
				DisplayInformation.AutoRotationPreferences = DisplayOrientations.Landscape;
			}
			catch (UnauthorizedAccessException ex)
			{
				Element?.RaiseMediaCaptureFailed($"The app was denied access to the camera or microphone. {ex.Message}");
				IsBusy = false;
				return;
			}

			try
			{
				Control.Source = mediaCapture;
				await mediaCapture.StartPreviewAsync();
				isPreviewing = false;
				IsBusy = false;
				Available = true;
			}
			catch (COMException)
			{
				Element?.RaiseMediaCaptureFailed("Camera device is not ready.");
			}
			catch (FileLoadException)
			{
				mediaCapture.CaptureDeviceExclusiveControlStatusChanged += CaptureDeviceExclusiveControlStatusChanged;
			}
		}

		async Task CleanupCameraAsync()
		{
			Available = false;
			IsBusy = true;
			if (mediaCapture == null)
				return;

			if (isPreviewing)
				await mediaCapture.StopPreviewAsync();

			if (mediaRecording != null)
				Element.RaiseMediaCaptured(new MediaCapturedEventArgs(video: await StopRecord()));

			await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
			{
				Control.Source = null;

				mediaCapture.CaptureDeviceExclusiveControlStatusChanged -= CaptureDeviceExclusiveControlStatusChanged;
				mediaCapture.Dispose();
				mediaCapture = null;
			});
			IsBusy = false;
		}

		protected override async void Dispose(bool disposing)
		{
			await CleanupCameraAsync();
			base.Dispose(disposing);
		}

		async void CaptureDeviceExclusiveControlStatusChanged(MediaCapture sender, MediaCaptureDeviceExclusiveControlStatusChangedEventArgs args)
		{
			if (args.Status == MediaCaptureDeviceExclusiveControlStatus.SharedReadOnlyAvailable)
				Element?.RaiseMediaCaptureFailed("The camera preview can't be displayed because another app has exclusive access");
			else if (args.Status == MediaCaptureDeviceExclusiveControlStatus.ExclusiveControlAvailable && !isPreviewing)
				await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await mediaCapture.StartPreviewAsync());
			IsBusy = false;
		}
	}
}