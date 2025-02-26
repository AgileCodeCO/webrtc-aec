using Microsoft.Extensions.Logging;
using NAudio.Wave;
using SIPSorceryMedia.Abstractions;
using WebRtcVadSharp;
using apm = AudioProcessingModuleCs.Media;

namespace WebRtc.EchoCancellation.ConsoleSender.Audio
{
    public class MicAudioSource : IAudioSource
    {
        private const int DEVICE_BITS_PER_SAMPLE = 16;
        private const int DEVICE_CHANNELS = 1;
        private const int INPUT_BUFFERS = 2;
        private const int AUDIO_SAMPLE_PERIOD_MILLISECONDS = 20;
        private const int AUDIO_INPUTDEVICE_INDEX = -1;

        /// <summary>
        /// Microphone input is sampled at 8KHz.
        /// </summary>
        public readonly static AudioSamplingRatesEnum DefaultAudioSourceSamplingRate = AudioSamplingRatesEnum.Rate8KHz;

        private ILogger logger = SIPSorcery.LogFactory.CreateLogger<MicAudioSource>();

        private WaveFormat _waveSourceFormat;

        /// <summary>
        /// Audio capture device.
        /// </summary>
        private WaveInEvent _waveInEvent;

        private WasapiLoopbackCapture _waveOut;
        private WaveFileWriter _wavWriter;

        private IAudioEncoder _audioEncoder;
        private MediaFormatManager<AudioFormat> _audioFormatManager;
        private apm.Dsp.WebRtc.WebRtcFilter _enhancer;

        private int _audioInDeviceIndex;
        private bool _disableSource;

        protected bool _isAudioSourceStarted;
        protected bool _isAudioSourcePaused;
        protected bool _isAudioSourceClosed;

        /// <summary>
        /// Not used by this audio source.
        /// </summary>
        public event EncodedSampleDelegate OnAudioSourceEncodedSample;

        /// <summary>
        /// This audio source DOES NOT generate raw samples. Subscribe to the encoded samples event
        /// to get samples ready for passing to the RTP transport layer.
        /// </summary>
        [Obsolete("The audio source only generates encoded samples.")]
        public event RawAudioSampleDelegate OnAudioSourceRawSample { add { } remove { } }

        public event SourceErrorDelegate OnAudioSourceError;

        /// <summary>
        /// Creates a new basic RTP session that captures and renders audio to/from the default system devices.
        /// </summary>
        /// <param name="audioEncoder">An audio encoder that can be used to encode and decode
        /// specific audio codecs.</param>
        /// <param name="externalSource">Optional. An external source to use in combination with the source
        /// provided by this end point. The application will need to signal which source is active.</param>
        /// <param name="disableSource">Set to true to disable the use of the audio source functionality, i.e.
        /// don't capture input from the microphone.</param>
        /// <param name="disableSink">Set to true to disable the use of the audio sink functionality, i.e.
        /// don't playback audio to the speaker.</param>
        public MicAudioSource(IAudioEncoder audioEncoder,
            int audioInDeviceIndex = AUDIO_INPUTDEVICE_INDEX,
            bool disableSource = false)
        {
            logger = SIPSorcery.LogFactory.CreateLogger<MicAudioSource>();

            _audioFormatManager = new MediaFormatManager<AudioFormat>(audioEncoder.SupportedFormats);
            _audioEncoder = audioEncoder;

            _audioInDeviceIndex = audioInDeviceIndex;
            _disableSource = disableSource;

            _enhancer = new apm.Dsp.WebRtc.WebRtcFilter(250,
                250,
                new apm.AudioFormat(samplesPerSecond: (int)DefaultAudioSourceSamplingRate, channels: DEVICE_CHANNELS, bitsPerSample: DEVICE_BITS_PER_SAMPLE),
                new apm.AudioFormat(),
                true,
                false,
                false);

            if (!_disableSource)
            {
                InitCaptureDevice(_audioInDeviceIndex, (int)DefaultAudioSourceSamplingRate);
            }
        }

        private void WaveOut_DataAvailable(object? sender, WaveInEventArgs e)
        {
            _wavWriter.Write(e.Buffer, 0, e.BytesRecorded);

            //byte[] buffer = e.Buffer.Take(e.BytesRecorded).ToArray();
            //short[] pcm = buffer.Where((x, i) => i % 2 == 0).Select((y, i) => BitConverter.ToInt16(buffer, i * 2)).ToArray();
            //byte[] encodedSample = _audioEncoder.EncodeAudio(pcm, _audioFormatManager.SelectedFormat);

            _enhancer.RegisterFramePlayed(e.Buffer);
        }

        public void RestrictFormats(Func<AudioFormat, bool> filter) => _audioFormatManager.RestrictFormats(filter);
        public List<AudioFormat> GetAudioSourceFormats() => _audioFormatManager.GetSourceFormats();

        public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;
        public bool IsAudioSourcePaused() => _isAudioSourcePaused;
        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample) =>
            throw new NotImplementedException();

        public void SetAudioSourceFormat(AudioFormat audioFormat)
        {
            _audioFormatManager.SetSelectedFormat(audioFormat);

            if (!_disableSource)
            {
                if (_waveSourceFormat.SampleRate != _audioFormatManager.SelectedFormat.ClockRate)
                {
                    // Reinitialise the audio capture device.
                    logger.LogDebug($"Windows audio end point adjusting capture rate from {_waveSourceFormat.SampleRate} to {_audioFormatManager.SelectedFormat.ClockRate}.");

                    InitCaptureDevice(_audioInDeviceIndex, _audioFormatManager.SelectedFormat.ClockRate);
                }
            }
        }

        public MediaEndPoints ToMediaEndPoints()
        {
            return new MediaEndPoints
            {
                AudioSource = (_disableSource) ? null : this
            };
        }

        /// <summary>
        /// Starts the media capturing/source devices.
        /// </summary>
        public Task StartAudio()
        {
            if (!_isAudioSourceStarted)
            {
                _isAudioSourceStarted = true;
                _waveInEvent?.StartRecording();
                _waveOut?.StartRecording();
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Closes the session.
        /// </summary>
        public Task CloseAudio()
        {
            if (!_isAudioSourceClosed)
            {
                _isAudioSourceClosed = true;

                if (_waveInEvent != null)
                {
                    _waveInEvent.DataAvailable -= LocalAudioSampleAvailable;
                    _waveInEvent.StopRecording();
                }

                if (_waveOut != null)
                {
                    _waveOut.DataAvailable -= WaveOut_DataAvailable;
                    _wavWriter.Dispose();
                    _waveOut.StopRecording();
                }
            }

            return Task.CompletedTask;
        }

        public Task PauseAudio()
        {
            _isAudioSourcePaused = true;
            _waveInEvent?.StopRecording();
            return Task.CompletedTask;
        }

        public Task ResumeAudio()
        {
            _isAudioSourcePaused = false;
            _waveInEvent?.StartRecording();
            return Task.CompletedTask;
        }

        private void InitCaptureDevice(int audioInDeviceIndex, int audioSourceSampleRate)
        {
            if (WaveInEvent.DeviceCount > 0)
            {
                if (WaveInEvent.DeviceCount > audioInDeviceIndex)
                {
                    if (_waveInEvent != null)
                    {
                        _waveInEvent.DataAvailable -= LocalAudioSampleAvailable;
                        _waveInEvent.StopRecording();
                    }

                    _waveSourceFormat = new WaveFormat(
                           audioSourceSampleRate,
                           DEVICE_BITS_PER_SAMPLE,
                           DEVICE_CHANNELS);

                    _waveInEvent = new WaveInEvent();
                    _waveInEvent.BufferMilliseconds = AUDIO_SAMPLE_PERIOD_MILLISECONDS;
                    _waveInEvent.NumberOfBuffers = INPUT_BUFFERS;
                    _waveInEvent.DeviceNumber = audioInDeviceIndex;
                    _waveInEvent.WaveFormat = _waveSourceFormat;
                    _waveInEvent.DataAvailable += LocalAudioSampleAvailable;

                    //Init loopback capture
                    _waveOut = new WasapiLoopbackCapture();
                    _waveOut.WaveFormat = new WaveFormat(16000, DEVICE_BITS_PER_SAMPLE, DEVICE_CHANNELS);

                    var outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "NAudio");
                    Directory.CreateDirectory(outputFolder);
                    var outputFilePath = Path.Combine(outputFolder, "recorded.wav");
                    _wavWriter = new WaveFileWriter(outputFilePath, _waveOut.WaveFormat);

                    _waveOut.DataAvailable += WaveOut_DataAvailable;
                }
                else
                {
                    logger.LogWarning($"The requested audio input device index {audioInDeviceIndex} exceeds the maximum index of {WaveInEvent.DeviceCount - 1}.");
                    OnAudioSourceError?.Invoke($"The requested audio input device index {audioInDeviceIndex} exceeds the maximum index of {WaveInEvent.DeviceCount - 1}.");
                }
            }
            else
            {
                logger.LogWarning("No audio capture devices are available.");
                OnAudioSourceError?.Invoke("No audio capture devices are available.");
            }
        }

        /// <summary>
        /// Event handler for audio sample being supplied by local capture device.
        /// </summary>
        private void LocalAudioSampleAvailable(object sender, WaveInEventArgs args)
        {
            // Note NAudio.Wave.WaveBuffer.ShortBuffer does not take into account little endian.
            // https://github.com/naudio/NAudio/blob/master/NAudio/Wave/WaveOutputs/WaveBuffer.cs
            //WaveBuffer wavBuffer = new WaveBuffer(args.Buffer.Take(args.BytesRecorded).ToArray());
            //byte[] encodedSample = _audioEncoder.EncodeAudio(wavBuffer.ShortBuffer, _audioFormatManager.SelectedFormat);
            //OnAudioSourceEncodedSample?.Invoke((uint)encodedSample.Length, encodedSample);

            //bool containsSpeech = DoesFrameContainSpeech(args.Buffer);
            //logger.LogInformation($"Frame contains speach: {containsSpeech}");

            //if (containsSpeech)
            {
                //byte[] buffer = args.Buffer.Take(args.BytesRecorded).ToArray();
                //short[] pcm = buffer.Where((x, i) => i % 2 == 0).Select((y, i) => BitConverter.ToInt16(buffer, i * 2)).ToArray();
                //byte[] encodedSample = _audioEncoder.EncodeAudio(pcm, _audioFormatManager.SelectedFormat);
                //OnAudioSourceEncodedSample?.Invoke((uint)encodedSample.Length, encodedSample);

                _enhancer.Write(args.Buffer);
                bool moreFrames;
                do
                {
                    short[] cancelBuffer = new short[args.BytesRecorded]; // contains cancelled audio signal
                    if (_enhancer.Read(cancelBuffer, out moreFrames, out var playedBuffer))
                    {
                        byte[] buffer = ShortsToBytes(cancelBuffer.Take(args.BytesRecorded).ToArray());
                        
                        short[] pcm = cancelBuffer.Where((x, i) => i % 2 == 0).Select((y, i) => BitConverter.ToInt16(buffer, i * 2)).ToArray();
                        byte[] encodedSample = _audioEncoder.EncodeAudio(pcm, _audioFormatManager.SelectedFormat);

                        if(playedBuffer != null)
                        {
                            byte[] buffer2 = ShortsToBytes(playedBuffer.Take(args.BytesRecorded).ToArray());
                            //_wavWriter.Write(buffer2, 0, buffer2.Length);

                            short[] pcm2 = playedBuffer.Where((x, i) => i % 2 == 0).Select((y, i) => BitConverter.ToInt16(buffer2, i * 2)).ToArray();
                            byte[] encodedSample2 = _audioEncoder.EncodeAudio(pcm2, _audioFormatManager.SelectedFormat);
                            //OnAudioSourceEncodedSample?.Invoke((uint)encodedSample2.Length, encodedSample2);
                        }

                        OnAudioSourceEncodedSample?.Invoke((uint)encodedSample.Length, encodedSample);
                    }
                } while (moreFrames);
            }
        }

        private bool DoesFrameContainSpeech(byte[] audioFrame)
        {
            using var vad = new WebRtcVad();
            return vad.HasSpeech(audioFrame, SampleRate.Is8kHz, FrameLength.Is20ms);
        }

        private static byte[] ShortsToBytes(short[] shorts)
        {
            byte[] bytes = new byte[shorts.Length * 2];
            Buffer.BlockCopy(shorts, 0, bytes, 0, bytes.Length);
            return bytes;
        }
    }
}
