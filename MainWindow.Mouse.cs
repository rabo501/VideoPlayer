using System;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
namespace VideoPlayer
{
    public partial class MainWindow
    {
        private VideoMouseInput? _videoMouse;
        private readonly DispatcherTimer _mouseSendTimer=new(){Interval=TimeSpan.FromMilliseconds(25)};
        private PointerGesture? _pointerFrame;
        private bool _sendingPointer;
        private void InitializeVideoMouseInput()
        {
            try
            {
                _videoMouse=new VideoMouseInput(new WindowInteropHelper(this).Handle,_videoPanel.Handle,
                    ()=>_ready&&!_opening&&!_closing&&!_shutdownRequested&&_sourceFilePath!=null&&Keyboard.Modifiers==ModifierKeys.None,
                    gesture=>
                    {
                        var old=_pointerFrame;
                        _pointerFrame=gesture with{Dx=gesture.Dx+(old?.Dx??0),Dy=gesture.Dy+(old?.Dy??0),Wheel=gesture.Wheel+(old?.Wheel??0)};
                    });
                _mouseSendTimer.Tick+=SendPointerFrame;_mouseSendTimer.Start();
            }
            catch(Exception ex){StatusText.Text="Nie uruchomiono sterowania myszą: "+ex.Message;}
        }
        private async void SendPointerFrame(object? sender,EventArgs e)
        {
            if(_sendingPointer)return;
            if(!_ready||_opening||_closing||_shutdownRequested){_pointerFrame=null;return;}
            if(_pointerFrame is not PointerGesture p)return;
            _pointerFrame=null;_sendingPointer=true;
            string F(double value)=>value.ToString("R",CultureInfo.InvariantCulture);
            try
            {
                await _mpvService.SendAsync("script-message","vp-pointer",F(p.Dx),F(p.Dy),F(p.Wheel),F(p.X),F(p.Y),F(p.Width),F(p.Height));
            }
            catch(Exception ex){if(!_closing)StatusText.Text="Sterowanie obrazem: "+ex.Message;}
            finally{_sendingPointer=false;}
        }
    }
}
