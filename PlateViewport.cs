using System;
namespace VideoPlayer
{
    internal readonly record struct PlateView(double Scale,double Left,double Top);
    internal readonly record struct PlateViewState(double Zoom,double PanX,double PanY);
    internal static class PlateViewport
    {
        internal static (double X,double Y) ConstrainPan(int width,int height,double areaWidth,double areaHeight,double zoom,double panX,double panY)
        {
            if(width<=0||height<=0||areaWidth<=0||areaHeight<=0)return(0,0);
            double scale=Math.Min(areaWidth/width,areaHeight/height)*Math.Clamp(zoom,1,16);
            double maxX=Math.Max(0,(width*scale-areaWidth)/2),maxY=Math.Max(0,(height*scale-areaHeight)/2);
            return(Math.Clamp(panX,-maxX,maxX),Math.Clamp(panY,-maxY,maxY));
        }
        internal static PlateView Place(int width,int height,double areaWidth,double areaHeight,PlateViewState state)
        {
            if(width<=0||height<=0||areaWidth<=0||areaHeight<=0)return new(0,0,0);
            double scale=Math.Min(areaWidth/width,areaHeight/height)*Math.Clamp(state.Zoom,1,16);
            var pan=ConstrainPan(width,height,areaWidth,areaHeight,state.Zoom,state.PanX,state.PanY);
            return new(scale,(areaWidth-width*scale)/2+pan.X,(areaHeight-height*scale)/2+pan.Y);
        }
        internal static PlateViewState ZoomAt(int width,int height,double areaWidth,double areaHeight,PlateViewState old,double targetZoom,double cursorX,double cursorY)
        {
            if(!double.IsFinite(targetZoom))throw new ArgumentOutOfRangeException(nameof(targetZoom));
            double zoom=Math.Clamp(targetZoom,1,16);var before=Place(width,height,areaWidth,areaHeight,old);
            if(before.Scale<=0)return new(zoom,0,0);
            double pixelX=(cursorX-before.Left)/before.Scale,pixelY=(cursorY-before.Top)/before.Scale;
            double scale=Math.Min(areaWidth/width,areaHeight/height)*zoom;
            double panX=cursorX-pixelX*scale-(areaWidth-width*scale)/2;
            double panY=cursorY-pixelY*scale-(areaHeight-height*scale)/2;
            var pan=ConstrainPan(width,height,areaWidth,areaHeight,zoom,panX,panY);
            return new(zoom,pan.X,pan.Y);
        }
    }
}
