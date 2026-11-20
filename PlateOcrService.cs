using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Tesseract;
namespace VideoPlayer
{
    internal sealed record PlateOcrCandidate(string Text,float Confidence);
    internal static class PlateOcrService
    {
        internal static string Normalize(string text)=>PlateOcrRules.Normalize(text);
        internal static IReadOnlyList<PlateOcrCandidate> Recognize(byte[] crop,CancellationToken ct)
        {
            string data=Path.Combine(AppContext.BaseDirectory,"tessdata");
            if(!File.Exists(Path.Combine(data,"eng.traineddata")))
                throw new FileNotFoundException("Brak pliku tessdata/eng.traineddata. Sprawdź, czy katalog tessdata znajduje się obok pliku VideoPlayer.exe.");

            // Wrapper charlesw/Tesseract 5.2.0 ładuje natywne biblioteki z podkatalogu x64.
            string nativeDirectory = Path.Combine(AppContext.BaseDirectory, "x64");
            string[] nativeLibraries = { "tesseract50.dll", "leptonica-1.82.0.dll" };
            foreach (string library in nativeLibraries)
            {
                string libraryPath = Path.Combine(nativeDirectory, library);
                if (!File.Exists(libraryPath))
                    throw new FileNotFoundException(
                        $"Brak natywnej biblioteki OCR: x64\\{library}. Skopiuj 64-bitowe biblioteki Tesseract 5.2.0 i Leptonica 1.82.0 do katalogu x64 obok VideoPlayer.exe.",
                        libraryPath);
            }
            using var input=new MemoryStream(crop);
            using var source=new Bitmap(input);
            double scale=Math.Min(Math.Clamp(110.0/source.Height,1.0,4.0),1800.0/source.Width);
            int w=Math.Max(1,(int)Math.Round(source.Width*scale)),h=Math.Max(1,(int)Math.Round(source.Height*scale));
            using var resized=new Bitmap(w+40,h+40,PixelFormat.Format24bppRgb);
            using(var g=Graphics.FromImage(resized))
            {g.Clear(Color.White);g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(source,20,20,w,h);}
            ct.ThrowIfCancellationRequested();
            var rect=new Rectangle(0,0,resized.Width,resized.Height);
            var bits=resized.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
            int stride=bits.Stride;byte[] pixels=new byte[stride*resized.Height];
            try{Marshal.Copy(bits.Scan0,pixels,0,pixels.Length);}finally{resized.UnlockBits(bits);}
            int[] histogram=new int[256];
            for(int y=0;y<resized.Height;y++)for(int x=0;x<resized.Width;x++)
            {int i=y*stride+x*3;byte v=(byte)((pixels[i]*114+pixels[i+1]*587+pixels[i+2]*299)/1000);histogram[v]++;pixels[i]=pixels[i+1]=pixels[i+2]=v;}
            int count=resized.Width*resized.Height,weight=0,threshold=127;double sum=0,leftSum=0,best=-1;
            for(int i=0;i<256;i++)sum+=i*(double)histogram[i];
            for(int i=0;i<256;i++)
            {weight+=histogram[i];if(weight==0)continue;int right=count-weight;if(right==0)break;leftSum+=i*(double)histogram[i];double delta=leftSum/weight-(sum-leftSum)/right;double variance=(double)weight*right*delta*delta;if(variance>best){best=variance;threshold=i;}}
            using var engine=new TesseractEngine(data,"eng",EngineMode.LstmOnly);
            engine.SetVariable("tessedit_char_whitelist","ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
            engine.SetVariable("user_defined_dpi","300");
            var candidates=new List<PlateOcrCandidate>();
            for(int mode=0;mode<3;mode++)
            {
                ct.ThrowIfCancellationRequested();byte[] output=(byte[])pixels.Clone();
                if(mode>0)for(int y=0;y<resized.Height;y++)for(int x=0;x<resized.Width;x++)
                {int i=y*stride+x*3;byte v=(byte)(output[i]>threshold?255:0);if(mode==2)v=(byte)(255-v);output[i]=output[i+1]=output[i+2]=v;}
                using var image=new Bitmap(resized.Width,resized.Height,PixelFormat.Format24bppRgb);
                var dst=image.LockBits(rect,ImageLockMode.WriteOnly,PixelFormat.Format24bppRgb);
                try{Marshal.Copy(output,0,dst.Scan0,output.Length);}finally{image.UnlockBits(dst);}
                using var png=new MemoryStream();image.Save(png,System.Drawing.Imaging.ImageFormat.Png);
                using var pix=Pix.LoadFromMemory(png.ToArray());
                foreach(var segmentation in new[]{PageSegMode.SingleLine,PageSegMode.RawLine})
                {
                    ct.ThrowIfCancellationRequested();
                    using var page=engine.Process(pix,segmentation);
                    string text=Normalize(page.GetText());
                    if(text.Length>=2&&text.Length<=12)candidates.Add(new(text,page.GetMeanConfidence()));
                }
            }
            ct.ThrowIfCancellationRequested();
            return candidates.GroupBy(x=>x.Text).Select(g=>g.OrderByDescending(x=>x.Confidence).First())
                .OrderByDescending(x=>x.Confidence).Take(5).ToArray();
        }
    }
}
