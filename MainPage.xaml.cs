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
using Microsoft.MixedReality.WebRTC;
using System.Diagnostics;
using Windows.Media.Capture;
using Windows.ApplicationModel;
using TestAppUwp.Video;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.MediaProperties;
using TestAppUwp;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace WebRTCUWP
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private PeerConnection _peerConnection;
        private DeviceAudioTrackSource _microphoneSource;
        private DeviceVideoTrackSource _webcamSource;
        private LocalAudioTrack _localAudioTrack;
        private LocalVideoTrack _localVideoTrack;
        private Transceiver _audioTransceiver;
        private Transceiver _videoTransceiver;
        private MediaStreamSource _localVideoSource;
        private VideoBridge _localVideoBridge = new VideoBridge(3);
        private bool _localVideoPlaying = false;
        private object _localVideoLock = new object();
        private NodeDssSignaler _signaler;

        private object _remoteVideoLock = new object();
        private bool _remoteVideoPlaying = false;
        private MediaStreamSource _remoteVideoSource;
        private VideoBridge _remoteVideoBridge = new VideoBridge(5);
        private RemoteVideoTrack _remoteVideoTrack;


        public MainPage()
        {
            this.InitializeComponent();
            this.Loaded += OnLoaded;
            Application.Current.Suspending += App_Suspending;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            var settings = new MediaCaptureInitializationSettings();
            settings.StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo;
            var caputre = new MediaCapture();
            await caputre.InitializeAsync(settings);

            IReadOnlyList<VideoCaptureDevice> deviceList = await DeviceVideoTrackSource.GetCaptureDevicesAsync();

            foreach (var device in deviceList)
            {
                Debugger.Log(0, "", $"Webcam {device.name} (id: {device.id})\n");
            }

            _peerConnection = new PeerConnection();

            var config = new PeerConnectionConfiguration
            {
                IceServers = new List<IceServer> { new IceServer { Urls = { "stun:stun.l.google.com:19302" } } }
            };
            await _peerConnection.InitializeAsync(config);
            _peerConnection.Connected += () => {
                Debugger.Log(0, "", "PeerConnection: connected.\n");
                Console.WriteLine("PeerConnection: connected");
            };
            _peerConnection.IceStateChanged += (IceConnectionState newState) => {
                Debugger.Log(0, "", $"ICE state: {newState}\n");
                Console.WriteLine($"ICE state: {newState}");
            };
            Debugger.Log(0, "", "Peer connection initialized successfully.\n");

            _peerConnection.VideoTrackAdded += (RemoteVideoTrack track) => {
                _remoteVideoTrack = track;
                _remoteVideoTrack.I420AVideoFrameReady += RemoteVideo_I420AFrameReady;
            };

            _webcamSource = await DeviceVideoTrackSource.CreateAsync();
            _webcamSource.I420AVideoFrameReady += LocalI420AFrameReady;
            var videoTrackConfig = new LocalVideoTrackInitConfig
            {
                trackName = "webcam_track"
            };

            _localVideoTrack = LocalVideoTrack.CreateFromSource(_webcamSource, videoTrackConfig);

            _microphoneSource = await DeviceAudioTrackSource.CreateAsync();

            var audioTrackConfig = new LocalAudioTrackInitConfig
            {
                trackName = "microphone_track"
            };
            _localAudioTrack = LocalAudioTrack.CreateFromSource(_microphoneSource, audioTrackConfig);

            _audioTransceiver = _peerConnection.AddTransceiver(MediaKind.Audio);
            _videoTransceiver = _peerConnection.AddTransceiver(MediaKind.Video);

            _audioTransceiver.LocalAudioTrack = _localAudioTrack;
            _videoTransceiver.LocalVideoTrack = _localVideoTrack;

            _peerConnection.LocalSdpReadytoSend += Peer_LocalSdpReadytoSend;
            _peerConnection.IceCandidateReadytoSend += Peer_IceCandidateReadytoSend;

            _signaler = new NodeDssSignaler()
            {
                HttpServerAddress = "http://localhost:3000/",
                LocalPeerId = "App1",
                RemotePeerId = "other"
            };

            _signaler.OnMessage += async (NodeDssSignaler.Message msg) =>
            {
                switch (msg.MessageType)
                {
                    case NodeDssSignaler.Message.WireMessageType.Offer:
                        await _peerConnection.SetRemoteDescriptionAsync(msg.ToSdpMessage());
                        _peerConnection.CreateAnswer();
                        break;
                    case NodeDssSignaler.Message.WireMessageType.Answer:
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                        _peerConnection.SetRemoteDescriptionAsync(msg.ToSdpMessage());
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                        break;
                    case NodeDssSignaler.Message.WireMessageType.Ice:
                        _peerConnection.AddIceCandidate(msg.ToIceCandidate());
                        break;
                    default:
                        break;
                }
            };
            _signaler.StartPollingAsync();
        }

        private void Peer_LocalSdpReadytoSend(SdpMessage message)
        {
            var msg = NodeDssSignaler.Message.FromSdpMessage(message);
            _signaler.SendMessageAsync(msg);
        }




        private void Peer_IceCandidateReadytoSend(IceCandidate iceCandidate)
        {
            var msg = NodeDssSignaler.Message.FromIceCandidate(iceCandidate);
            _signaler.SendMessageAsync(msg);
        }

        private void LocalI420AFrameReady(I420AVideoFrame frame)
        {
            lock (_localVideoLock)
            {
                if (!_localVideoPlaying)
                {
                    _localVideoPlaying = true;

                    uint width = frame.width;
                    uint height = frame.height;


                    RunOnMainThread(() =>
                    {
                        int framerate = 30;
                        _localVideoSource = CreateI420VideoStreamSource(
                            width, height, framerate);
                        var localVideoPlayer = new MediaPlayer();
                        localVideoPlayer.Source = MediaSource.CreateFromMediaStreamSource(_localVideoSource);
                        localVideoPlayerElement.SetMediaPlayer(localVideoPlayer);
                        localVideoPlayer.Play();
                    });

                }

            }
            _localVideoBridge.HandleIncomingVideoFrame(frame);
        }

        private void RunOnMainThread(Windows.UI.Core.DispatchedHandler handler)
        {
            if (Dispatcher.HasThreadAccess)
            {
                handler.Invoke();
            }
            else
            {
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, handler);
            }
        }

        private MediaStreamSource CreateI420VideoStreamSource(uint width, uint height, int framerate)
        {
            if (width == 0)
            {
                throw new ArgumentException("Invalid zero width for video.", "width");
            }
            if (height == 0)
            {
                throw new ArgumentException("Invalid zero height for video.", "height");
            }

            var videPorperties = VideoEncodingProperties.CreateUncompressed(
                MediaEncodingSubtypes.Iyuv, width, height);
            var videoStreamDesc = new VideoStreamDescriptor(videPorperties);
            videoStreamDesc.EncodingProperties.FrameRate.Numerator = (uint)framerate;
            videoStreamDesc.EncodingProperties.FrameRate.Denominator = 1;
            videoStreamDesc.EncodingProperties.Bitrate = ((uint)framerate * width * height * 12);
            var videoStreamSource = new MediaStreamSource(videoStreamDesc);
            videoStreamSource.BufferTime = TimeSpan.Zero;
            videoStreamSource.SampleRequested += OnMediaStreamSourceRequested;
            videoStreamSource.IsLive = true;
            videoStreamSource.CanSeek = false;
            return videoStreamSource;
        }

        private void RemoteVideo_I420AFrameReady(I420AVideoFrame frame)
        {
            lock (_remoteVideoLock)
            {
                if (!_remoteVideoPlaying)
                {
                    _remoteVideoPlaying = true;
                    uint width = frame.width;
                    uint height = frame.height;
                    RunOnMainThread(() =>
                    {
                        // Bridge the remote video track with the remote media player UI
                        int framerate = 30; // assumed, for lack of an actual value
                        _remoteVideoSource = CreateI420VideoStreamSource(width, height,
                            framerate);
                        var remoteVideoPlayer = new MediaPlayer();
                        remoteVideoPlayer.Source = MediaSource.CreateFromMediaStreamSource(
                            _remoteVideoSource);
                        remoteVideoPlayerElement.SetMediaPlayer(remoteVideoPlayer);
                        remoteVideoPlayer.Play();
                    });
                }
            }
            _remoteVideoBridge.HandleIncomingVideoFrame(frame);
        }

        private void OnMediaStreamSourceRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            VideoBridge videoBridge;
            if (sender == _localVideoSource)
                videoBridge = _localVideoBridge;
            else if (sender == _remoteVideoSource)
                videoBridge = _remoteVideoBridge;
            else
                return;
            videoBridge.TryServeVideoFrame(args);
        }

        private void App_Suspending(Object sender, SuspendingEventArgs e)
        {
            if (_peerConnection != null)
            {
                _peerConnection.Close();
                _peerConnection.Dispose();
                _peerConnection = null;
            }

            if (_signaler != null)
            {
                _signaler.StopPollingAsync();
                _signaler = null;
            }

            localVideoPlayerElement.SetMediaPlayer(null);
            remoteVideoPlayerElement.SetMediaPlayer(null);
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            _peerConnection.CreateOffer();
        }
    }
}