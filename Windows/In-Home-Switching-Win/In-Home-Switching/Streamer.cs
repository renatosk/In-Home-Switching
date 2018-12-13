﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Drawing.Imaging;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using ScpDriverInterface;

namespace InHomeSwitching.Window
{
    class Streamer
    {
        private string ffmpeg_args = " -y -f rawvideo -pixel_format rgb32 -framerate 300 -video_size {0}x{1} -i pipe: -f h264 -vf scale=1280x720 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -profile:v baseline -x264-params \"nal-hrd=cbr\" -b:v {2}M -minrate {2}M -maxrate {2}M -bufsize 2M tcp://{3}:2222 ";
        private int resX;
        private int resY;
        public string ip;
        public int quality;
        private Thread renderThread = null;
        private Thread inputThread = null;
        public bool running = false;
        public bool started = false;

        ImageConverter converter = new ImageConverter();
        private static DesktopDuplicator desktopDuplicator = new DesktopDuplicator(0);

        Process proc = null;

        ScpBus scp = new ScpBus();
        X360Controller ctrl = new X360Controller();

        private void initConnection()
        {
            DesktopFrame frame = null;
            while (true)
            {
                try
                {
                    frame = desktopDuplicator.GetLatestFrame();
                    break;
                }
                catch {
                    desktopDuplicator = new DesktopDuplicator(0);
                };

            }
            resX = frame.DesktopImage.Width;
            resY = frame.DesktopImage.Height;


            if (proc != null)
            {
                try { proc.Kill(); }
                catch { };
            }
            proc = new Process();

            proc.StartInfo.FileName = @"ffmpeg.exe";
            proc.StartInfo.Arguments = string.Format(ffmpeg_args, resX, resY, quality, ip);

            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.RedirectStandardInput = true;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.CreateNoWindow = true;
            proc.Start();

        }


        private unsafe void StreamerLoop()
        {
            DesktopFrame frame = null;
            while (true)
            {
                if (!started)
                {
                    running = false;
                    Thread.Sleep(100);
                    continue;
                }
                try
                {
                    frame = desktopDuplicator.GetLatestFrame();
                }
                catch
                {
                    desktopDuplicator = new DesktopDuplicator(0);
                    continue;
                }
                if (frame == null)
                {
                    continue;
                }
                if (resX != frame.DesktopImage.Width || resY != frame.DesktopImage.Height)
                {
                    initConnection();
                    continue;
                }
                BitmapData bData = frame.DesktopImage.LockBits(new Rectangle(0, 0, frame.DesktopImage.Width, frame.DesktopImage.Height), ImageLockMode.ReadWrite, frame.DesktopImage.PixelFormat);
                byte* scan0 = (byte*)bData.Scan0.ToPointer();
                var size = bData.Width * bData.Height * 4;
                UnmanagedMemoryStream ms = new UnmanagedMemoryStream(scan0, size);
                try
                {
                    proc.StandardInput.BaseStream.Flush();
                    ms.CopyTo(proc.StandardInput.BaseStream);
                    running = true;
                }
                catch
                {

                    Stop();
                }
                frame.DesktopImage.UnlockBits(bData);

            }
        }


        public struct InputPkg
        {
            public ulong HeldKeys;
            public short LJoyX;
            public short LJoyY;
            public short RJoyX;
            public short RJoyY;
        }

        public enum InputKeys : ulong
        {
            A = 1,
            B = 1 << 1,
            X = 1 << 2,
            Y = 1 << 3,
            LS = 1 << 4,
            RS = 1 << 5,
            L = 1 << 6,
            R = 1 << 7,
            ZL = 1 << 8,
            ZR = 1 << 9,
            Plus = 1 << 10,
            Minus = 1 << 11,
            Left = 1 << 12,
            Up = 1 << 13,
            Right = 1 << 14,
            Down = 1 << 15
        }

        TcpClient client = null;
        NetworkStream serverStream = null;
        private InputPkg ReadPkg()
        {

            var buf = new byte[0x10];
            var pkg = new InputPkg();

            serverStream.Read(buf, 0, 0x10);
            pkg.HeldKeys = BitConverter.ToUInt64(buf, 0);
            pkg.LJoyX = BitConverter.ToInt16(buf, 8);
            pkg.LJoyY = BitConverter.ToInt16(buf, 10);
            pkg.RJoyX = BitConverter.ToInt16(buf, 12);
            pkg.RJoyY = BitConverter.ToInt16(buf, 14);

            return pkg;
        }

        private void InputLoop()
        {
            while (true)
            {
                if (!started)
                {
                    Thread.Sleep(100);
                    continue;
                }
                try
                {
                    client = new System.Net.Sockets.TcpClient();
                    client.Connect(ip, 2223);
                    serverStream = client.GetStream();
                    serverStream.ReadTimeout = 2000;
                }
                catch
                {
                    continue;
                }

                while (true)
                {
                    InputPkg pkg;
                    try
                    {

                        pkg = ReadPkg();

                    }
                    catch { break; }

                    void map(InputKeys inkey, X360Buttons outkey)
                    {
                        if ((pkg.HeldKeys & (ulong)inkey) > 0)
                            ctrl.Buttons |= outkey;
                        else ctrl.Buttons &= ~outkey;
                    }

                    map(InputKeys.A, X360Buttons.B);
                    map(InputKeys.B, X360Buttons.A);
                    map(InputKeys.X, X360Buttons.Y);
                    map(InputKeys.Y, X360Buttons.X);

                    map(InputKeys.L, X360Buttons.LeftBumper);
                    map(InputKeys.R, X360Buttons.RightBumper);

                    map(InputKeys.LS, X360Buttons.LeftStick);
                    map(InputKeys.RS, X360Buttons.RightStick);

                    map(InputKeys.Plus, X360Buttons.Start);
                    map(InputKeys.Minus, X360Buttons.Back);

                    map(InputKeys.Up, X360Buttons.Up);
                    map(InputKeys.Down, X360Buttons.Down);
                    map(InputKeys.Left, X360Buttons.Left);
                    map(InputKeys.Right, X360Buttons.Right);

                    if ((pkg.HeldKeys & (ulong)InputKeys.ZL) > 0)
                        ctrl.LeftTrigger = byte.MaxValue;
                    else ctrl.LeftTrigger = byte.MinValue;

                    if ((pkg.HeldKeys & (ulong)InputKeys.ZR) > 0)
                        ctrl.RightTrigger = byte.MaxValue;
                    else ctrl.RightTrigger = byte.MinValue;

                    ctrl.LeftStickX = pkg.LJoyX;
                    ctrl.LeftStickY = pkg.LJoyY;

                    ctrl.RightStickX = pkg.RJoyX;
                    ctrl.RightStickY = pkg.RJoyY;

                    scp.Report(1, ctrl.GetReport());
                }
            }
        }

        public void Start(string ip, int quality)
        {
            if (started)
            {
                Stop();
                Thread.Sleep(100);
            }

            this.ip = ip;
            this.quality = quality;
            initConnection();
            started = true;

        }

        public void Stop()
        {
            try
            {
                client.Close();
            }
            catch { }
            try
            {
                proc.Kill();
            }
            catch { }
            started = false;
            running = false;
        }

        public Streamer()
        {
            scp.PlugIn(1);


            ThreadStart inputRef = new ThreadStart(InputLoop);
            inputThread = new Thread(inputRef);
            inputThread.Start();


            ThreadStart renderRef = new ThreadStart(StreamerLoop);
            renderThread = new Thread(renderRef);
            renderThread.Start();
        }
    }
}