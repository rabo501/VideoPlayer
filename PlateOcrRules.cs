using System;
using System.Text.RegularExpressions;
namespace VideoPlayer
{
    internal static class PlateOcrRules
    {
        internal static string Normalize(string text)=>Regex.Replace(text.ToUpperInvariant(),"[^A-Z0-9]","");
        internal static bool IsValidManual(string text)
        {text=text.Trim().Replace(" ","").Replace("-","").ToUpperInvariant();return text.Length>=2&&text.Length<=12&&Normalize(text)==text;}
        internal static (int X,int Y,int Width,int Height) Crop(int width,int height,double areaWidth,double areaHeight,double startX,double startY,double endX,double endY)
        {
            if(width<=0||height<=0||areaWidth<=0||areaHeight<=0)throw new ArgumentOutOfRangeException(nameof(width));
            double scale=Math.Min(areaWidth/width,areaHeight/height);
            double offsetX=(areaWidth-width*scale)/2,offsetY=(areaHeight-height*scale)/2;
            int left=Math.Clamp((int)Math.Floor((Math.Min(startX,endX)-offsetX)/scale),0,width-1);
            int top=Math.Clamp((int)Math.Floor((Math.Min(startY,endY)-offsetY)/scale),0,height-1);
            int right=Math.Clamp((int)Math.Ceiling((Math.Max(startX,endX)-offsetX)/scale),left+1,width);
            int bottom=Math.Clamp((int)Math.Ceiling((Math.Max(startY,endY)-offsetY)/scale),top+1,height);
            return(left,top,right-left,bottom-top);
        }
    }
}
