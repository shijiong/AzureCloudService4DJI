using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

//DJI SDK
using DJI.WindowsSDK;
using Windows.UI.Xaml.Media.Imaging;
using DJIVideoParser;

// For Threading timer
using Windows.System.Threading;
using System.Threading;
using System.Threading.Tasks;

// Azure IoTHub Device Client SDK
using Microsoft.Azure.Devices.Client;
// Required for Json Format Handling
using System.Runtime.Serialization.Json;

// Required for Azure Storage
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Table;
using System.Net.Http;

//Azure Cognitive Services
using Microsoft.Azure.CognitiveServices.Vision.Face;
using Microsoft.Azure.CognitiveServices.Vision.Face.Models;

// For text encoding
using System.Text;
using System.Diagnostics;
using Windows.UI.Popups;

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using System.Numerics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml.Hosting;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace MyDJISDKDemo
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    /// 
    public class DroneEntity : TableEntity
    {
        public DroneEntity()
        {
            this.PartitionKey = "DJIMavicAir";
            this.RowKey = Guid.NewGuid().ToString();

            MeasurementTime = System.DateTime.Now;
            VelocityX = 0;
            VelocityY = 0;
            VelocityZ = 0;

        }
        public System.DateTime MeasurementTime { get; set; }
        public double VelocityX { get; set; }
        public double VelocityY { get; set; }
        public double VelocityZ { get; set; }
    }
    public sealed partial class MainPage : Page
    {
        //use videoParser to decode raw data.
        private DJIVideoParser.Parser videoParser;

        //private data
        private double velocityX = 0;
        private double velocityY = 0;
        private double velocityZ = 0;

        //transmit timer to Storage Table
        private static ThreadPoolTimer timerDataTransfer;

        //transmit timer to IotHub
        private static ThreadPoolTimer timerIotHubTransfer;

        //connect string
        private const string connectionstring = "HostName=*********.azure-devices.net;DeviceId=DJIMavicAir;SharedAccessKey=**************************=";

        //Azure device client
        DeviceClient deviceClient;

        //Velocity
        Velocity3D aircraftVelocity3D;

        // Capture API objects.
        private SizeInt32 _lastSize;
        private GraphicsCaptureItem _item;
        private Direct3D11CaptureFramePool _framePool;
        private GraphicsCaptureSession _session;

        // Non-API related members.
        private CanvasDevice _canvasDevice;
        private CompositionGraphicsDevice _compositionGraphicsDevice;
        private Compositor _compositor;
        private CompositionDrawingSurface _surface;
        private CanvasBitmap _currentFrame;
        private string _screenshotFilename = "test.png";

        //Face API
        Size size_image;
        DetectedFace[] faces;
        //Face API Key
        string key_face = "*****************************************";
        string face_apiroot = "https://westus.api.cognitive.microsoft.com/"; // For instance: https://southcentralus.api.cognitive.microsoft.com/

        public MainPage()
        {
            this.InitializeComponent();
            DJISDKManager.Instance.SDKRegistrationStateChanged += Instance_SDKRegistrationEvent;

            //Replace with your registered App Key. Make sure your App Key matched your application's package name on DJI developer center.
            DJISDKManager.Instance.RegisterApp("***************");

            //Init device client
            deviceClient = DeviceClient.CreateFromConnectionString(connectionstring, TransportType.Http1);

            //Init Screen Capture
            Setup();

            //spMain.Visibility=Visibility.Collapsed;
        }

        private async void Instance_SDKRegistrationEvent(SDKRegistrationState state, SDKError resultCode)
        {
            if (resultCode == SDKError.NO_ERROR)
            {
                System.Diagnostics.Debug.WriteLine("Register app successfully.");

                //Must in UI Thread
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                {
                    //Raw data and decoded data listener
                    if (videoParser == null)
                    {
                        videoParser = new DJIVideoParser.Parser();
                        videoParser.Initialize(delegate (byte[] data)
                        {
                            //Note: This function must be called because we need DJI Windows SDK to help us to parse frame data.
                            return DJISDKManager.Instance.VideoFeeder.ParseAssitantDecodingInfo(0, data);
                        });
                        //Set the swapChainPanel to display and set the decoded data callback.
                        videoParser.SetSurfaceAndVideoCallback(0, 0, swapChainPanel, ReceiveDecodedData);
                        DJISDKManager.Instance.VideoFeeder.GetPrimaryVideoFeed(0).VideoDataUpdated += OnVideoPush;
                    }
                    //get the camera type and observe the CameraTypeChanged event.
                    DJISDKManager.Instance.ComponentManager.GetCameraHandler(0, 0).CameraTypeChanged += OnCameraTypeChanged;
                    var type = await DJISDKManager.Instance.ComponentManager.GetCameraHandler(0, 0).GetCameraTypeAsync();
                    OnCameraTypeChanged(this, type.value);
                    //set the VelocityChanged event
                    DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).VelocityChanged += OnVelocityChanged;
                    var typeVelocity = await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).GetVelocityAsync();
                    OnVelocityChanged(this, typeVelocity.value);
                });
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("Register SDK failed, the error is: ");
                System.Diagnostics.Debug.WriteLine(resultCode.ToString());
            }
        }

        private void Setup()
        {
            _canvasDevice = new CanvasDevice();

            _compositionGraphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(
                Window.Current.Compositor,
                _canvasDevice);

            _compositor = Window.Current.Compositor;

            _surface = _compositionGraphicsDevice.CreateDrawingSurface(
                new Size(400, 400),
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                DirectXAlphaMode.Premultiplied);    // This is the only value that currently works with
                                                    // the composition APIs.

            var visual = _compositor.CreateSpriteVisual();
            visual.RelativeSizeAdjustment = Vector2.One;
            var brush = _compositor.CreateSurfaceBrush(_surface);
            brush.HorizontalAlignmentRatio = 1.0f;
            brush.VerticalAlignmentRatio = 1.0f;
            brush.Stretch = CompositionStretch.Uniform;
            visual.Brush = brush;
            ElementCompositionPreview.SetElementChildVisual(this, visual);
        }


        //raw data
        void OnVideoPush(VideoFeed sender, byte[] bytes)
        {
            videoParser.PushVideoData(0, 0, bytes, bytes.Length);
        }

        //Decode data. Do nothing here. This function would return a bytes array with image data in RGBA format.
        async void ReceiveDecodedData(byte[] data, int width, int height)
        {
        }

        //We need to set the camera type of the aircraft to the DJIVideoParser. After setting camera type, DJIVideoParser would correct the distortion of the video automatically.
        private void OnCameraTypeChanged(object sender, CameraTypeMsg? value)
        {
            if (value != null)
            {
                switch (value.Value.value)
                {
                    case CameraType.MAVIC_2_ZOOM:
                        this.videoParser.SetCameraSensor(AircraftCameraType.Mavic2Zoom);
                        break;
                    case CameraType.MAVIC_2_PRO:
                        this.videoParser.SetCameraSensor(AircraftCameraType.Mavic2Pro);
                        break;
                    default:
                        this.videoParser.SetCameraSensor(AircraftCameraType.Others);
                        break;
                }

            }
        }

        private async void OnVelocityChanged(object sender, Velocity3D? value)
        {
            if (value != null)
            {

                aircraftVelocity3D = value.Value;
                velocityX = aircraftVelocity3D.x;
                velocityY = aircraftVelocity3D.y;
                velocityZ = aircraftVelocity3D.z;
                //Must in UI Thread
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                {
                    VelocityXTB.Text = velocityX.ToString()+"m/s";
                    VelocityYTB.Text = velocityY.ToString() + "m/s";
                    VelocityZTB.Text = velocityZ.ToString() + "m/s";
                });
            }
        }

        private async void StartShootPhoto_Click(object sender, RoutedEventArgs e)
        {
            if (DJISDKManager.Instance.ComponentManager != null)
            {
                var retCode = await DJISDKManager.Instance.ComponentManager.GetCameraHandler(0, 0).StartShootPhotoAsync();
                if (retCode != SDKError.NO_ERROR)
                {
                    OutputTB.Text = "Failed to shoot photo, result code is " + retCode.ToString();
                }
                else
                {
                    OutputTB.Text = "Shoot photo successfully";
                }
            }
            else
            {
                OutputTB.Text = "SDK hasn't been activated yet.";
            }
        }

        private async void StartRecordVideo_Click(object sender, RoutedEventArgs e)
        {
            if (DJISDKManager.Instance.ComponentManager != null)
            {
                var retCode = await DJISDKManager.Instance.ComponentManager.GetCameraHandler(0, 0).StartRecordAsync();
                if (retCode != SDKError.NO_ERROR)
                {
                    OutputTB.Text = "Failed to record video, result code is " + retCode.ToString();
                }
                else
                {
                    OutputTB.Text = "Record video successfully";
                }
            }
            else
            {
                OutputTB.Text = "SDK hasn't been activated yet.";
            }
        }

        private async void StopRecordVideo_Click(object sender, RoutedEventArgs e)
        {
            if (DJISDKManager.Instance.ComponentManager != null)
            {
                var retCode = await DJISDKManager.Instance.ComponentManager.GetCameraHandler(0, 0).StopRecordAsync();
                if (retCode != SDKError.NO_ERROR)
                {
                    OutputTB.Text = "Failed to stop record video, result code is " + retCode.ToString();
                }
                else
                {
                    OutputTB.Text = "Stop record video successfully";
                }
            }
            else
            {
                OutputTB.Text = "SDK hasn't been activated yet.";
            }
        }

        private async void SetCameraWorkModeToShootPhoto_Click(object sender, RoutedEventArgs e)
        {
            SetCameraWorkMode(CameraWorkMode.SHOOT_PHOTO);
        }

        private async void SetCameraModeToRecord_Click(object sender, RoutedEventArgs e)
        {
            SetCameraWorkMode(CameraWorkMode.RECORD_VIDEO);
        }

        private async void SetCameraWorkMode(CameraWorkMode mode)
        {
            if (DJISDKManager.Instance.ComponentManager != null)
            {
                CameraWorkModeMsg workMode = new CameraWorkModeMsg
                {
                    value = mode,
                };
                var retCode = await DJISDKManager.Instance.ComponentManager.GetCameraHandler(0, 0).SetCameraWorkModeAsync(workMode);
                if (retCode != SDKError.NO_ERROR)
                {
                    OutputTB.Text = "Set camera work mode to " + mode.ToString() + "failed, result code is " + retCode.ToString();
                }
            }
            else
            {
                OutputTB.Text = "SDK hasn't been activated yet.";
            }
        }

        private async Task SendDataToAzureIoTHub(string text)
        {
            try
            {               
                //  var text = "Hellow, Windows 10!";
                var msg = new Message(Encoding.UTF8.GetBytes(text));

                await deviceClient.SendEventAsync(msg);
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.ToString());
            }
        }

        public T Deserialize<T>(string json)
        {
            var _Bytes = Encoding.Unicode.GetBytes(json);
            using (MemoryStream _Stream = new MemoryStream(_Bytes))
            {
                var _Serializer = new DataContractJsonSerializer(typeof(T));
                return (T)_Serializer.ReadObject(_Stream);
            }
        }

        public string Serialize(object instance)
        {
            try
            {
                using (MemoryStream _Stream = new MemoryStream())
                {
                    var _Serializer = new DataContractJsonSerializer(instance.GetType());
                    _Serializer.WriteObject(_Stream, instance);
                    _Stream.Position = 0;
                    using (StreamReader _Reader = new StreamReader(_Stream))
                    { return _Reader.ReadToEnd(); }
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.ToString());
                return null;
            }
        }

        private void IoTButton_Click(object sender, RoutedEventArgs e)
        {
            timerIotHubTransfer = ThreadPoolTimer.CreatePeriodicTimer(dataIoTHubTick, TimeSpan.FromMilliseconds(Convert.ToInt32(5000)));
        }

        private async void dataIoTHubTick(ThreadPoolTimer timer)
        {
            try
            {
                // Create a new customer entity.
                DroneEntity ent = new DroneEntity();

                ent.MeasurementTime = System.DateTime.Now;
                ent.VelocityX = velocityX;
                ent.VelocityY = velocityY;
                ent.VelocityZ = velocityZ;

                String JsonData = Serialize(ent);
                await SendDataToAzureIoTHub(JsonData);
            }
            catch (Exception ex)
            {
                MessageDialog dialog = new MessageDialog("Error sending to IoTHub: " + ex.Message);
                await dialog.ShowAsync();
            }
        }

        private void AzureStorageButton_Click(object sender, RoutedEventArgs e)
        {
            timerDataTransfer = ThreadPoolTimer.CreatePeriodicTimer(dataTransmitterTick, TimeSpan.FromMilliseconds(Convert.ToInt32(5000)));
        }

        private async void dataTransmitterTick(ThreadPoolTimer timer)
        {
            try
            {
                CloudStorageAccount storageAccount = CloudStorageAccount.Parse("DefaultEndpointsProtocol=https;AccountName=********************;AccountKey=************************************=");

                // Create the table client.
                CloudTableClient tableClient = storageAccount.CreateCloudTableClient();

                // Create the CloudTable object that represents the " AccelerometerTable " table.
                CloudTable table = tableClient.GetTableReference("DJITable");
                await table.CreateIfNotExistsAsync();

                // Create a new customer entity.
                DroneEntity ent = new DroneEntity();
                ent.MeasurementTime = System.DateTime.Now;
                ent.VelocityX = velocityX;
                ent.VelocityY = velocityY;
                ent.VelocityZ = velocityZ;

                // Create the TableOperation that inserts the customer entity.
                TableOperation insertOperation = TableOperation.Insert(ent);
                // Execute the insert operation.
                await table.ExecuteAsync(insertOperation);
            }
            catch (Exception ex)
            {
                MessageDialog dialog = new MessageDialog("Error sending to Azure: " + ex.Message);
                await dialog.ShowAsync();
            }
        }

        private async void ScreenshotButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            var picker = new GraphicsCapturePicker();
            GraphicsCaptureItem item = await picker.PickSingleItemAsync();
            if (item != null)
            {
                _item = item;
                _lastSize = _item.Size;

                if (_lastSize.Height != 0)
                {
                    _framePool = Direct3D11CaptureFramePool.Create(
                       _canvasDevice, // D3D device
                       DirectXPixelFormat.B8G8R8A8UIntNormalized, // Pixel format
                       2, // Number of frames
                       _item.Size); // Size of the buffers

                    _session = _framePool.CreateCaptureSession(_item);
                    _session.StartCapture();
                    Thread.Sleep(200);
                    var frame = _framePool.TryGetNextFrame();
                    if (frame != null)
                    {
                        // Convert our D3D11 surface into a Win2D object.
                        CanvasBitmap canvasBitmap = CanvasBitmap.CreateFromDirect3D11Surface(
                            _canvasDevice,
                            frame.Surface);

                        _currentFrame = canvasBitmap;
                        await SaveImageAsync(_screenshotFilename, _currentFrame);
                    }
                    _session?.Dispose();
                    _framePool?.Dispose();
                    _item = null;
                    _session = null;
                    _framePool = null;
                }
            }

            
        }

        private async Task SaveImageAsync(string filename, CanvasBitmap frame)
        {
            StorageFolder pictureFolder = KnownFolders.SavedPictures;

            StorageFile file = await pictureFolder.CreateFileAsync(
                filename,
                CreationCollisionOption.ReplaceExisting);

            using (var fileStream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                await frame.SaveAsync(fileStream, CanvasBitmapFileFormat.Png, 1f);
            }

            var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
            var stream_send = stream.CloneStream();
            var stream_send2 = stream.CloneStream();
            var image = new BitmapImage();
            image.SetSource(stream);
            imgPhoto.Source = image;
            size_image = new Size(image.PixelWidth, image.PixelHeight);

            ringLoading.IsActive = true;

            //Face service
            FaceClient f_client = new FaceClient(
                new ApiKeyServiceClientCredentials(key_face),
                new System.Net.Http.DelegatingHandler[] { });  // need to provide and endpoint and a delegate. key_face, face_apiroot);
            f_client.Endpoint = face_apiroot;

            var requiedFaceAttributes = new FaceAttributeType[] {
                                FaceAttributeType.Age,
                                FaceAttributeType.Gender,
                                FaceAttributeType.Smile,
                                FaceAttributeType.FacialHair,
                                FaceAttributeType.HeadPose,
                                FaceAttributeType.Emotion,
                                FaceAttributeType.Glasses
                                };
            var faces_task = await f_client.Face.DetectWithStreamAsync(stream_send.AsStream(), true, true, requiedFaceAttributes);

            faces = faces_task.ToArray();

            if (faces != null)
            {
                DisplayFacesData(faces);
            }

            //hide preview
            if (swapChainPanel.Visibility == Visibility.Collapsed)
            {
                swapChainPanel.Visibility = Visibility.Visible;
                spMain.Visibility = Visibility.Collapsed;
                ShowPreviewButton.Content = "Hide Preview";
            }
            else
            {
                swapChainPanel.Visibility = Visibility.Collapsed;
                spMain.Visibility = Visibility.Visible;
                ShowPreviewButton.Content = "Show Preview";
            }

            ringLoading.IsActive = false;
        }

        /// <summary>
        /// Display Face Data
        /// </summary>
        /// <param name="result"></param>
        private void DisplayFacesData(DetectedFace[] faces, bool init = true)
        {
            if (faces == null)
                return;

            cvasMain.Children.Clear();
            var offset_h = 0.0; var offset_w = 0.0;
            var p = 0.0;
            var d = cvasMain.ActualHeight / cvasMain.ActualWidth;
            var d2 = size_image.Height / size_image.Width;
            if (d < d2)
            {
                offset_h = 0;
                offset_w = (cvasMain.ActualWidth - cvasMain.ActualHeight / d2) / 2;
                p = cvasMain.ActualHeight / size_image.Height;
            }
            else
            {
                offset_w = 0;
                offset_h = (cvasMain.ActualHeight - cvasMain.ActualWidth / d2) / 2;
                p = cvasMain.ActualWidth / size_image.Width;
            }
            if (faces != null)
            {
                int count = 1;
                foreach (var face in faces)
                {
                    Windows.UI.Xaml.Shapes.Rectangle rect = new Windows.UI.Xaml.Shapes.Rectangle();
                    rect.Width = face.FaceRectangle.Width * p;
                    rect.Height = face.FaceRectangle.Height * p;
                    Canvas.SetLeft(rect, face.FaceRectangle.Left * p + offset_w);
                    Canvas.SetTop(rect, face.FaceRectangle.Top * p + offset_h);
                    rect.Stroke = new SolidColorBrush(Colors.Orange);
                    rect.StrokeThickness = 3;

                    cvasMain.Children.Add(rect);

                    TextBlock txt = new TextBlock();
                    txt.Foreground = new SolidColorBrush(Colors.Orange);
                    txt.Text = "#" + count;
                    Canvas.SetLeft(txt, face.FaceRectangle.Left * p + offset_w);
                    Canvas.SetTop(txt, face.FaceRectangle.Top * p + offset_h - 20);
                    cvasMain.Children.Add(txt);
                    count++;
                }
            }          
        }

        private void btnShow_Click(object sender, RoutedEventArgs e)
        {
            if (swapChainPanel.Visibility == Visibility.Collapsed)
            {
                swapChainPanel.Visibility = Visibility.Visible;
                spMain.Visibility = Visibility.Collapsed;
                ShowPreviewButton.Content = "Hide Preview";
            }
            else
            {
                swapChainPanel.Visibility = Visibility.Collapsed;
                spMain.Visibility = Visibility.Visible;
                ShowPreviewButton.Content = "Show Preview";
            }
        }
    }

}
