using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;
public static class IconBuilder {
    public static int Main(string[] args) {
        int[] sizes={16,24,32,48,64,128,256}; List<byte[]> images=new List<byte[]>();
        foreach(int size in sizes) using(Bitmap bitmap=new Bitmap(size,size)) { using(Graphics g=Graphics.FromImage(bitmap)) { g.Clear(Color.Transparent); Theme.Mark(g,new Rectangle(0,0,size,size)); } using(MemoryStream stream=new MemoryStream()) { bitmap.Save(stream,ImageFormat.Png); images.Add(stream.ToArray()); } }
        using(BinaryWriter writer=new BinaryWriter(File.Create(args[0]))) {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length); int offset=6+16*sizes.Length;
            for(int i=0;i<sizes.Length;i++) { writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(images[i].Length); writer.Write(offset); offset+=images[i].Length; }
            foreach(byte[] image in images) writer.Write(image);
        }
        return 0;
    }
}
