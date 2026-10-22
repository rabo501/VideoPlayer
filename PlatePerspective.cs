using System;
namespace VideoPlayer
{
    internal readonly record struct PlatePoint(double X,double Y);
    internal sealed record PlatePixels(int Width,int Height,byte[] Pixels);
    internal static class PlatePerspective
    {
        private static double Distance(PlatePoint a,PlatePoint b)=>Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
        internal static bool IsValid(PlatePoint[] p,int width,int height)
        {
            if(p.Length!=4||width<2||height<2)return false;
            foreach(var a in p)if(!double.IsFinite(a.X)||!double.IsFinite(a.Y)||a.X<0||a.Y<0||a.X>width-1||a.Y>height-1)return false;
            double area=0;
            for(int i=0;i<4;i++)
            {
                var a=p[i];var b=p[(i+1)%4];var c=p[(i+2)%4];
                if(Distance(a,b)<4||(b.X-a.X)*(c.Y-b.Y)-(b.Y-a.Y)*(c.X-b.X)<=0.01)return false;
                area+=a.X*b.Y-b.X*a.Y;
            }
            return area>=64;
        }
        internal static double[] Transform(PlatePoint[] p)
        {
            if(p.Length!=4)throw new ArgumentException("Potrzebne są cztery narożniki.");
            double dx1=p[1].X-p[2].X,dx2=p[3].X-p[2].X,dx3=p[0].X-p[1].X+p[2].X-p[3].X;
            double dy1=p[1].Y-p[2].Y,dy2=p[3].Y-p[2].Y,dy3=p[0].Y-p[1].Y+p[2].Y-p[3].Y;
            double g=0,h=0;
            if(Math.Abs(dx3)+Math.Abs(dy3)>1e-10)
            {
                double det=dx1*dy2-dx2*dy1;
                if(Math.Abs(det)<1e-10)throw new ArgumentException("Narożniki nie wyznaczają poprawnego obszaru.");
                g=(dx3*dy2-dx2*dy3)/det;h=(dx1*dy3-dx3*dy1)/det;
            }
            return new[]{p[1].X-p[0].X+g*p[1].X,p[3].X-p[0].X+h*p[3].X,p[0].X,
                p[1].Y-p[0].Y+g*p[1].Y,p[3].Y-p[0].Y+h*p[3].Y,p[0].Y,g,h};
        }
        internal static PlatePoint Map(double[] m,double u,double v)
        {
            double denominator=m[6]*u+m[7]*v+1;
            if(Math.Abs(denominator)<1e-10)throw new ArgumentException("Nieprawidłowa perspektywa zaznaczenia.");
            return new((m[0]*u+m[1]*v+m[2])/denominator,(m[3]*u+m[4]*v+m[5])/denominator);
        }
        internal static PlatePixels Rectify(byte[] bgra,int width,int height,PlatePoint[] p)
        {
            if(!IsValid(p,width,height))throw new ArgumentException("Dopasuj cztery narożniki bez krzyżowania boków. Obszar nie może być zbyt mały.");
            if(bgra.LongLength!=(long)width*height*4)throw new ArgumentException("Nieprawidłowy rozmiar obrazu BGRA.");
            double w=Math.Max(Distance(p[0],p[1]),Distance(p[3],p[2]))+1;
            double h=Math.Max(Distance(p[0],p[3]),Distance(p[1],p[2]))+1;
            double scale=Math.Min(1,Math.Min(1600/w,600/h));
            int ow=Math.Max(2,(int)Math.Round(w*scale)),oh=Math.Max(2,(int)Math.Round(h*scale));
            byte[] dst=new byte[ow*oh*4];var m=Transform(p);
            for(int y=0;y<oh;y++)for(int x=0;x<ow;x++)
            {
                var q=Map(m,x/(double)(ow-1),y/(double)(oh-1));
                double sx=Math.Clamp(q.X,0,width-1),sy=Math.Clamp(q.Y,0,height-1);
                int x0=(int)Math.Floor(sx),y0=(int)Math.Floor(sy),x1=Math.Min(x0+1,width-1),y1=Math.Min(y0+1,height-1);
                double fx=sx-x0,fy=sy-y0;int a=(y0*width+x0)*4,b=(y0*width+x1)*4,c=(y1*width+x0)*4,d=(y1*width+x1)*4,o=(y*ow+x)*4;
                for(int channel=0;channel<3;channel++)
                    dst[o+channel]=(byte)Math.Clamp((int)Math.Round((bgra[a+channel]*(1-fx)+bgra[b+channel]*fx)*(1-fy)+(bgra[c+channel]*(1-fx)+bgra[d+channel]*fx)*fy),0,255);
                dst[o+3]=255;
            }
            return new(ow,oh,dst);
        }
    }
}
