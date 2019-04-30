﻿namespace Kermalis.VGMusicStudio.Core.NDS.DSE
{
    internal class Channel
    {
        public readonly byte Index;

        public Track Owner;
        public EnvelopeState State;
        public byte RootKey, Key, NoteVelocity;
        public sbyte Panpot; // Not necessary
        public int Velocity;
        public byte Volume;
        public ushort BaseTimer, Timer;
        public uint NoteLength;

        private byte attack;
        private int sustain;
        private ushort decay;
        private ushort release;

        private int pos;
        private short prevLeft, prevRight;

        // PCM8, PCM16, ADPCM
        private SWD.SampleBlock sample;
        // PCM8, PCM16
        private int dataOffset;
        // ADPCM
        private ADPCMDecoder adpcmDecoder;
        private short adpcmLoopLastSample;
        private short adpcmLoopStepIndex;

        public Channel(byte i)
        {
            Index = i;
        }

        public bool StartPCM(SWD localswd, SWD masterswd, byte voice, int key, uint noteLength)
        {
            SWD.IProgramInfo programInfo = localswd.Programs.ProgramInfos[voice];
            if (programInfo != null)
            {
                for (int i = 0; i < programInfo.SplitEntries.Length; i++)
                {
                    SWD.ISplitEntry split = programInfo.SplitEntries[i];
                    if (key >= split.LowKey && key <= split.HighKey)
                    {
                        sample = masterswd.Samples[split.SampleId];
                        Key = (byte)key;
                        RootKey = split.SampleRootKey;
                        BaseTimer = (ushort)(Utils.ARM7_CLOCK / sample.WavInfo.SampleRate);
                        SetAttack(0x7F - sample.WavInfo.Attack);
                        SetDecay(0x7F - sample.WavInfo.Decay);
                        SetSustain(sample.WavInfo.Sustain);
                        SetRelease(0x7F - sample.WavInfo.Release);
                        if (sample.WavInfo.SampleFormat == SampleFormat.ADPCM)
                        {
                            adpcmDecoder = new ADPCMDecoder(sample.Data);
                        }
                        State = EnvelopeState.Attack;
                        Velocity = -92544;
                        pos = 0;
                        prevLeft = prevRight = 0;
                        NoteLength = noteLength;
                        return true;
                    }
                }
            }
            return false;
        }

        public void Stop()
        {
            if (Owner != null)
            {
                Owner.Channels.Remove(this);
            }
            Owner = null;
            Volume = 0;
        }

        // TODO
        public void SetAttack(int a)
        {
            attack = SDAT.Utils.AttackTable[a];
        }
        public void SetDecay(int d)
        {
            decay = SDAT.Utils.DecayTable[d];
        }
        public void SetSustain(byte s)
        {
            sustain = SDAT.Utils.SustainTable[s];
        }
        public void SetRelease(int r)
        {
            release = SDAT.Utils.DecayTable[r];
        }
        public void StepEnvelope()
        {
            switch (State)
            {
                case EnvelopeState.Attack:
                {
                    Velocity = attack * Velocity / 0xFF;
                    if (Velocity == 0)
                    {
                        State = EnvelopeState.Decay;
                    }
                    break;
                }
                case EnvelopeState.Decay:
                {
                    Velocity -= decay;
                    if (Velocity <= sustain)
                    {
                        State = EnvelopeState.Sustain;
                        Velocity = sustain;
                    }
                    break;
                }
                case EnvelopeState.Release:
                {
                    Velocity -= release;
                    if (Velocity < -92544)
                    {
                        Velocity = -92544;
                    }
                    break;
                }
            }
        }

        public void Process(out short left, out short right)
        {
            if (Timer == 0)
            {
                left = prevLeft;
                right = prevRight;
            }
            else
            {
                int numSamples = (pos + 0x100) / Timer;
                pos = (pos + 0x100) % Timer;
                // prevLeft and prevRight are stored because numSamples can be 0.
                for (int i = 0; i < numSamples; i++)
                {
                    short samp = 0;
                    switch (sample.WavInfo.SampleFormat)
                    {
                        case SampleFormat.PCM8:
                        {
                            // If hit end
                            if (dataOffset >= sample.Data.Length)
                            {
                                if (sample.WavInfo.Loop)
                                {
                                    dataOffset = (int)(sample.WavInfo.LoopStart * 4);
                                }
                                else
                                {
                                    left = right = prevLeft = prevRight = 0;
                                    Stop();
                                    return;
                                }
                            }
                            samp = (short)((sbyte)sample.Data[dataOffset++] << 8);
                            break;
                        }
                        case SampleFormat.PCM16:
                        {
                            // If hit end
                            if (dataOffset >= sample.Data.Length)
                            {
                                if (sample.WavInfo.Loop)
                                {
                                    dataOffset = (int)(sample.WavInfo.LoopStart * 4);
                                }
                                else
                                {
                                    left = right = prevLeft = prevRight = 0;
                                    Stop();
                                    return;
                                }
                            }
                            samp = (short)(sample.Data[dataOffset++] | (sample.Data[dataOffset++] << 8));
                            break;
                        }
                        case SampleFormat.ADPCM:
                        {
                            // If just looped
                            if (adpcmDecoder.DataOffset == sample.WavInfo.LoopStart * 4 && !adpcmDecoder.OnSecondNibble)
                            {
                                adpcmLoopLastSample = adpcmDecoder.LastSample;
                                adpcmLoopStepIndex = adpcmDecoder.StepIndex;
                            }
                            // If hit end
                            if (adpcmDecoder.DataOffset >= sample.Data.Length && !adpcmDecoder.OnSecondNibble)
                            {
                                if (sample.WavInfo.Loop)
                                {
                                    adpcmDecoder.DataOffset = (int)(sample.WavInfo.LoopStart * 4);
                                    adpcmDecoder.StepIndex = adpcmLoopStepIndex;
                                    adpcmDecoder.LastSample = adpcmLoopLastSample;
                                    adpcmDecoder.OnSecondNibble = false;
                                }
                                else
                                {
                                    left = right = prevLeft = prevRight = 0;
                                    Stop();
                                    return;
                                }
                            }
                            samp = adpcmDecoder.GetSample();
                            break;
                        }
                    }
                    samp = (short)(samp * Volume / 0x7F);
                    prevLeft = (short)(samp * (-Panpot + 0x40) / 0x80);
                    prevRight = (short)(samp * (Panpot + 0x40) / 0x80);
                }
                left = prevLeft;
                right = prevRight;
            }
        }
    }
}
