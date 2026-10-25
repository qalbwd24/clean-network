using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Collections.Generic;

public static class MakeIcon {
    public static void Main(string[] args) {
        string dir=Path.Combine(Path.GetFullPath(args[0]),"assets");Directory.CreateDirectory(dir);
        using(var master=new Bitmap(1024,1024,PixelFormat.Format32bppArgb)) {
            using(var g=Graphics.FromImage(master)) {
                g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;g.ScaleTransform(16,16);
                var bounds=new RectangleF(2,2,60,60);float r=30;using(var rounded=new GraphicsPath()) {
                    rounded.AddArc(2,2,r,r,180,90);rounded.AddArc(32,2,r,r,270,90);rounded.AddArc(32,32,r,r,0,90);rounded.AddArc(2,32,r,r,90,90);rounded.CloseFigure();
                    using(var gradient=new LinearGradientBrush(bounds,Color.FromArgb(74,118,255),Color.FromArgb(39,73,204),45))g.FillPath(gradient,rounded);
                }
                var points=new[]{new PointF(16,30),new PointF(28,42),new PointF(48,22)};
                using(var pen=new Pen(Color.White,5){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round})g.DrawLines(pen,points);
                foreach(var p in points)g.FillEllipse(Brushes.White,p.X-4.5f,p.Y-4.5f,9,9);
            }
            var frames=new List<byte[]>();int[] sizes={16,24,32,48,64,128,256};
            foreach(int size in sizes)using(var small=new Bitmap(size,size,PixelFormat.Format32bppArgb)){
                using(var g=Graphics.FromImage(small)){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.DrawImage(master,0,0,size,size);}
                using(var memory=new MemoryStream()){small.Save(memory,ImageFormat.Png);frames.Add(memory.ToArray());}
                if(size==256)small.Save(Path.Combine(dir,"app.png"),ImageFormat.Png);
            }
            using(var file=File.Create(Path.Combine(dir,"app.ico")))using(var writer=new BinaryWriter(file)){
                writer.Write((short)0);writer.Write((short)1);writer.Write((short)sizes.Length);int offset=6+16*sizes.Length;
                for(int i=0;i<sizes.Length;i++){writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)0);writer.Write((byte)0);writer.Write((short)1);writer.Write((short)32);writer.Write(frames[i].Length);writer.Write(offset);offset+=frames[i].Length;}
                foreach(var frame in frames)writer.Write(frame);
            }
        }
    }
}
