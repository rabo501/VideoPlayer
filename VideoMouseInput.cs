using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
namespace VideoPlayer
{
    internal readonly record struct PointerGesture(double Dx,double Dy,double Wheel,double X,double Y,int Width,int Height);
    // WM_INPUT reaches our HWND even when mpv's child window consumes normal mouse events.
    // No global hooks, no input suppression, no forwarding of background-app gestures.
    internal sealed class VideoMouseInput : IDisposable
    {
        private readonly HwndSource _source;
        private readonly IntPtr _video;
        private readonly Func<bool> _enabled;
        private readonly Action<PointerGesture> _receive;
        private bool _dragging,_disposed;
        private Point _previous;
        public VideoMouseInput(IntPtr owner,IntPtr video,Func<bool> enabled,Action<PointerGesture> receive)
        {
            _source=HwndSource.FromHwnd(owner)??throw new InvalidOperationException("Brak okna wejściowego.");
            _video=video;_enabled=enabled;_receive=receive;
            var devices=new[]{new Device{Page=1,Usage=2,Flags=0x100,Target=owner}};
            if(!RegisterRawInputDevices(devices,1,(uint)Marshal.SizeOf<Device>()))throw new Win32Exception(Marshal.GetLastWin32Error());
            _source.AddHook(Hook);
        }
        private IntPtr Hook(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled)
        {
            if(msg!=0xFF||_disposed)return IntPtr.Zero;
            uint size=0,header=(uint)(8+2*IntPtr.Size);
            if(GetRawInputData(lParam,0x10000003,IntPtr.Zero,ref size,header)==uint.MaxValue||size<header+24||size>262144)return IntPtr.Zero;
            IntPtr data=Marshal.AllocHGlobal((int)size);
            try
            {
                if(GetRawInputData(lParam,0x10000003,data,ref size,header)==uint.MaxValue||Marshal.ReadInt32(data)!=0)return IntPtr.Zero;
                int offset=(int)header;
                ushort flags=unchecked((ushort)Marshal.ReadInt16(data,offset+4));
                if(!_enabled()||GetAncestor(GetForegroundWindow(),2)!=GetAncestor(hwnd,2)||!GetCursorPos(out var cursor)||!GetClientRect(_video,out var rect))
                {_dragging=false;return IntPtr.Zero;}
                var origin=new Point();if(!ClientToScreen(_video,ref origin)){_dragging=false;return IntPtr.Zero;}
                IntPtr hit=WindowFromPoint(cursor);
                bool inside=cursor.X>=origin.X&&cursor.Y>=origin.Y&&cursor.X<origin.X+rect.Right&&cursor.Y<origin.Y+rect.Bottom&&(hit==_video||IsChild(_video,hit));
                if(!inside||rect.Right<=0||rect.Bottom<=0){_dragging=false;return IntPtr.Zero;}
                if((flags&2)!=0)_dragging=false;
                if((flags&1)!=0){_dragging=true;_previous=cursor;}
                double dx=0,dy=0;
                if(_dragging)
                {
                    if((GetAsyncKeyState(1)&0x8000)==0)_dragging=false;
                    else {dx=(cursor.X-_previous.X)/(double)rect.Right;dy=(cursor.Y-_previous.Y)/(double)rect.Bottom;_previous=cursor;}
                }
                double wheel=(flags&0x400)!=0?Marshal.ReadInt16(data,offset+6)/120.0:0;
                if(dx!=0||dy!=0||wheel!=0)_receive(new PointerGesture(dx,dy,wheel,(cursor.X-origin.X)/(double)rect.Right,(cursor.Y-origin.Y)/(double)rect.Bottom,rect.Right,rect.Bottom));
            }
            finally{Marshal.FreeHGlobal(data);}
            // Leave WM_INPUT unhandled, allowing standard foreground input cleanup.
            return IntPtr.Zero;
        }
        public void Dispose()
        {
            if(_disposed)return;_disposed=true;_source.RemoveHook(Hook);
            RegisterRawInputDevices(new[]{new Device{Page=1,Usage=2,Flags=1,Target=IntPtr.Zero}},1,(uint)Marshal.SizeOf<Device>());
        }
        [StructLayout(LayoutKind.Sequential)]private struct Device{public ushort Page,Usage;public uint Flags;public IntPtr Target;}
        [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
        [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
        [DllImport("user32.dll",SetLastError=true)]private static extern bool RegisterRawInputDevices([In]Device[] devices,uint count,uint size);
        [DllImport("user32.dll")]private static extern uint GetRawInputData(IntPtr raw,uint command,IntPtr data,ref uint size,uint headerSize);
        [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")]private static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
        [DllImport("user32.dll")]private static extern bool ClientToScreen(IntPtr hwnd,ref Point point);
        [DllImport("user32.dll")]private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")]private static extern bool IsChild(IntPtr parent,IntPtr child);
        [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]private static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
        [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    }
}
