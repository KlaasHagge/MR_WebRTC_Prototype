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
//WebRTC
using Microsoft.MixedReality.WebRTC;
//Debugger: print to log
using System.Diagnostics;
//Cam- and mic-access
using Windows.Media.Capture;
//Resource-clean-up
using Windows.ApplicationModel;
//Resources for media-handling
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.MediaProperties;
using System.Threading.Tasks;

namespace MR_WebRTC_Prototype
{
    public sealed partial class MainPage : Page
    {
        private PeerConnection peerConnection;

        //Local video
        private MediaStreamSource localVideoSource;
        private VideoBridge localVideoBridge = new VideoBridge(3);
        private bool localVideoPlaying = false;
        private object localVideoLock = new object();

        //Remote video
        private MediaStreamSource remoteVideoSource;
        private VideoBridge remoteVideoBridge = new VideoBridge(5);
        private bool remoteVideoPlaying = false;
        private object remoteVideoLock = new object();

        private NodeDssSignaler signaler;

        //Id strings
        string localId;
        string remoteId;
        string nodeDssServerIp;

        List<VideoCaptureDevice> camList;
        VideoCaptureDevice cam;

        public MainPage()
        {
            this.InitializeComponent();

            //Event fires, when ui finish loading
            this.Loaded += OnLoaded;
            //Event fires, when app turns into suspended-state
            Application.Current.Suspending += App_Suspending;
        }

        //Loaded-EventHandler of ui: executes after ui-loading
        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            //Request access to mic and cam
            MediaCaptureInitializationSettings settings = new MediaCaptureInitializationSettings();
            settings.StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo;

            MediaCapture capture = new MediaCapture();
            await capture.InitializeAsync(settings);

            //Get list of cams
            camList = await PeerConnection.GetVideoCaptureDevicesAsync();

            //Print list to log
            foreach (var cam in camList)
            {
                Debugger.Log(0, "", $"Webcam: {cam.name} (id: {cam.id})\n");
            }

            //Ask user for ids (show MessageBox)
            await ShowIdInputBoxAsync();

            //New PeerConnection (Access to WebRTC)
            peerConnection = new PeerConnection();
            //Create PeerConnection-config
            PeerConnectionConfiguration config = new PeerConnectionConfiguration()
            {
                IceServers = new List<IceServer>()
                {
                    //Using google stun server for testing
                    new IceServer() {Urls = {"stun:stun.l.google.com:19302"}}
                }
            };

            //Initialize PeerContection
            await peerConnection.InitializeAsync(config);
            //Event fires, when local video frame is captured and ready for rendering
            peerConnection.I420LocalVideoFrameReady += Peer_LocalI420FrameReady;
            //Event fires, when remote video frame is receved and ready for rendering
            peerConnection.I420RemoteVideoFrameReady += Peer_RemoteI420FrameReady;
            //Events fires, when SdpMessage is ready for sending
            peerConnection.LocalSdpReadytoSend += Peer_LocalSdpReadytoSend;
            //Event fires, when IceCandidate is ready for sending
            peerConnection.IceCandidateReadytoSend += Peer_IceCandidateReadytoSend;
            //Set DebuggingLog-messages
            peerConnection.Connected += () => Debugger.Log(0, "", "PeerConnection: connected\n");
            peerConnection.IceStateChanged += (IceConnectionState newState) => Debugger.Log(0, "", $"ICE state: {newState}\n");

            Debugger.Log(0, "", "Peer conection initialized successfully\n");

            //Adds cam-tracks from standart (first) devices [add parameter to specify cam-device or -specifications]
            await peerConnection.AddLocalVideoTrackAsync(new PeerConnection.LocalVideoTrackSettings() { videoDevice = cam });
            //Same for mic [no specifications possible: always uses the first mic in list]
            await peerConnection.AddLocalAudioTrackAsync();

            //Initialize the signaler (Properties from MessageBox)
            signaler = new NodeDssSignaler()
            {
                HttpServerAddress = nodeDssServerIp,
                LocalPeerId = localId,
                RemotePeerId = remoteId
            };
            signaler.OnMessage += (NodeDssSignaler.Message msg) =>
            {
                switch (msg.MessageType)
                {
                    case NodeDssSignaler.Message.WireMessageType.Offer:
                        peerConnection.SetRemoteDescription("offer", msg.Data);
                        peerConnection.CreateAnswer();
                        break;

                    case NodeDssSignaler.Message.WireMessageType.Answer:
                        peerConnection.SetRemoteDescription("answer", msg.Data);
                        break;

                    case NodeDssSignaler.Message.WireMessageType.Ice:
                        string[] parts = msg.Data.Split(new string[] { msg.IceDataSeparator }, StringSplitOptions.RemoveEmptyEntries);
                        //Changing order of parts
                        string sdpMid = parts[2];
                        int sdpMlineindex = int.Parse(parts[1]);
                        string candidate = parts[0];
                        peerConnection.AddIceCandidate(sdpMid, sdpMlineindex, candidate);
                        break;
                }
            };
            signaler.StartPollingAsync();

        }

        //Suspending-EventHandler of app: executes when app turns into suspending-state
        private void App_Suspending(object sender, SuspendingEventArgs e)
        {
            if (peerConnection != null)
            {
                //Closing of PeerConnection
                peerConnection.Close();
                //Disposing of PeerConnection-Resources
                peerConnection.Dispose();
                peerConnection = null;

                Debugger.Log(0, "", "Peer conection disposed successfully\n");
            }

            //Disposing of MediaPlayers
            Mpe_localVideo.SetMediaPlayer(null);
            Mpe_remoteVideo.SetMediaPlayer(null);

            //Disposing of signaler
            if (signaler != null)
            {
                signaler.StopPollingAsync();
                signaler = null;
            }
        }

        //Method to build connection to video-device and connecting to video
        private MediaStreamSource CreateI420VideoStreamSource(uint width, uint height, int framerate)
        {
            if (width == 0)
                throw new ArgumentException("Invalid zero width for video", "width");
            if (height == 0)
                throw new ArgumentException("Invalid zero height for video", "height");

            VideoEncodingProperties videoProperties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Iyuv, width, height);
            VideoStreamDescriptor videoStreamDesc = new VideoStreamDescriptor(videoProperties);

            videoStreamDesc.EncodingProperties.FrameRate.Numerator = (uint)framerate;
            videoStreamDesc.EncodingProperties.FrameRate.Denominator = 1;
            // Bitrate in bits per second : framerate * frame pixel size * I420=12bpp
            videoStreamDesc.EncodingProperties.Bitrate = ((uint)framerate * width * height * 12);

            MediaStreamSource videoStreamSource = new MediaStreamSource(videoStreamDesc)
            {
                BufferTime = TimeSpan.Zero,
                // Enables optimizations for live sources
                IsLive = true,
                // Cannot seek live WebRTC video stream
                CanSeek = false
            };

            //Event called by request for new frame (?)
            videoStreamSource.SampleRequested += OnMediaStreamSourceRequested;

            return videoStreamSource;
        }


        //EventHandler for StreamSource-requests
        private void OnMediaStreamSourceRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            VideoBridge videoBridge;
            if (sender == localVideoSource)
                videoBridge = localVideoBridge;
            else if (sender == remoteVideoSource)
                videoBridge = remoteVideoBridge;
            else
                return;
            videoBridge.TryServeVideoFrame(args);
        }


        //EventHandler for sending sdp-messages
        private void Peer_LocalSdpReadytoSend(string type, string sdp)
        {
            NodeDssSignaler.Message msg = new NodeDssSignaler.Message()
            {
                MessageType = NodeDssSignaler.Message.WireMessageTypeFromString(type),
                Data = sdp,
                IceDataSeparator = "|"
            };
            signaler.SendMessageAsync(msg);
        }


        //EventHandler for sending IceCandidate
        private void Peer_IceCandidateReadytoSend(string candidate, int sdpMlineindex, string sdpMid)
        {
            NodeDssSignaler.Message msg = new NodeDssSignaler.Message()
            {
                MessageType = NodeDssSignaler.Message.WireMessageType.Ice,
                Data = $"{candidate}|{sdpMlineindex}|{sdpMid}",
                IceDataSeparator = "|"
            };
        }


        //EventHandler for showing remote video in ui
        private void Peer_RemoteI420FrameReady(I420AVideoFrame frame)
        {
            lock (remoteVideoLock)
            {
                if (!remoteVideoPlaying)
                {
                    remoteVideoPlaying = true;

                    // Capture the resolution into local variable useable from the lambda below
                    uint width = frame.width;
                    uint height = frame.height;

                    // Defer UI-related work to the main UI thread
                    RunOnMainThread(() =>
                    {
                        // Bridge the local video track with the local media player UI
                        int framerate = 30; // for lack of an actual value
                        remoteVideoSource = CreateI420VideoStreamSource(width, height, framerate);
                        var remoteVideoPlayer = new MediaPlayer();
                        remoteVideoPlayer.Source = MediaSource.CreateFromMediaStreamSource(remoteVideoSource);
                        Mpe_remoteVideo.SetMediaPlayer(remoteVideoPlayer);
                        remoteVideoPlayer.Play();
                    });
                }
            }
            remoteVideoBridge.HandleIncomingVideoFrame(frame);
        }


        //EventHandler for showing local video in ui
        private void Peer_LocalI420FrameReady(I420AVideoFrame frame)
        {
            lock (localVideoLock)
            {
                if (!localVideoPlaying)
                {
                    localVideoPlaying = true;

                    // Capture the resolution into local variable useable from the lambda below
                    uint width = frame.width;
                    uint height = frame.height;

                    // Defer UI-related work to the main UI thread
                    RunOnMainThread(() =>
                    {
                        // Bridge the local video track with the local media player UI
                        int framerate = 30; // for lack of an actual value
                        localVideoSource = CreateI420VideoStreamSource(width, height, framerate);
                        var localVideoPlayer = new MediaPlayer();
                        localVideoPlayer.Source = MediaSource.CreateFromMediaStreamSource(localVideoSource);
                        Mpe_localVideo.SetMediaPlayer(localVideoPlayer);
                        localVideoPlayer.Play();
                    });
                }
            }
            localVideoBridge.HandleIncomingVideoFrame(frame);
        }


        //Helper for running actions in Main-Ui-Thread
        private void RunOnMainThread(Windows.UI.Core.DispatchedHandler handler)
        {
            if (Dispatcher.HasThreadAccess)
            {
                handler.Invoke();
            }
            else
            {
                // Note: use a discard "_" to silence CS4014 warning
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, handler);
            }
        }

        private void Btn_CreateOffer_Click(object sender, RoutedEventArgs e)
        {
            peerConnection.CreateOffer();
        }


        //MessageBox (Asks for IDs/DSS-IP/Cam)
        private async Task ShowIdInputBoxAsync()
        {
            StackPanel Spl_Dialog = new StackPanel();
            TextBlock Tbl_nodeDssServerIp = new TextBlock()
            {
                Text = "Insert IP of Node-DSS-Server",
                Height = 32,
                Width = 300
            };
            TextBox Tbx_nodeDssServerIp = new TextBox()
            {
                Text = "http://127.0.0.1:3000/",
                AcceptsReturn = false,
                Height = 32,
                Width = 300
            };
            TextBlock Tbl_localId = new TextBlock()
            {
                Text = "Insert local Id:",
                Height = 32,
                Width = 300
            };
            TextBox Tbx_localId = new TextBox()
            {
                Text = "MR_WebRTC_Prototype_01",
                AcceptsReturn = false,
                Height = 32,
                Width = 300
            };
            TextBlock Tbl_remoteId = new TextBlock()
            {
                Text = "Insert remote Id:",
                Height = 32,
                Width = 300
            };
            TextBox Tbx_remoteId = new TextBox()
            {
                Text = "MR_WebRTC_Prototype_02",
                AcceptsReturn = false,
                Height = 32,
                Width = 300
            };
            TextBlock Tbl_camDevice = new TextBlock()
            {
                Text = "Choose Camera",
                Height = 32,
                Width = 300
            };
            ComboBox Cbb_camDevice = new ComboBox
            {
                Height = 32,
                Width = 300,
                SelectedIndex = 0                
            };
            foreach (var cam in camList)
            {
                Cbb_camDevice.Items.Add(cam);
            }
            Spl_Dialog.Children.Add(Tbl_nodeDssServerIp);
            Spl_Dialog.Children.Add(Tbx_nodeDssServerIp);
            Spl_Dialog.Children.Add(Tbl_localId);
            Spl_Dialog.Children.Add(Tbx_localId);
            Spl_Dialog.Children.Add(Tbl_remoteId);
            Spl_Dialog.Children.Add(Tbx_remoteId);
            Spl_Dialog.Children.Add(Tbl_camDevice);
            Spl_Dialog.Children.Add(Cbb_camDevice);

            ContentDialog dialog = new ContentDialog()
            {
                Content = Spl_Dialog,
                Title = "Id Settings",
                IsPrimaryButtonEnabled = true,
                IsSecondaryButtonEnabled = false,
                PrimaryButtonText = "OK"
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                nodeDssServerIp = Tbx_nodeDssServerIp.Text;
                localId = Tbx_localId.Text;
                remoteId = Tbx_remoteId.Text;
                cam = (VideoCaptureDevice)Cbb_camDevice.SelectedItem;
            }
        }
    }
}
