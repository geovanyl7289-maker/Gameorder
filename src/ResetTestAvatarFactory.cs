using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Gameorder;

// Small original geometric portraits. No real Steam avatar, user image, or network is used.
internal static class ResetTestAvatarFactory
{
    internal static byte[] Create(int variant)
    {
        const int size=64;
        byte[][] colors=[[205,222,255],[208,239,220],[255,225,196]];
        byte[][] clothes=[[77,114,214],[64,157,122],[217,125,68]];
        var background=colors[(variant-1)%3];var shirt=clothes[(variant-1)%3];
        var pixels=new byte[size*(size*4+1)];
        for(int y=0;y<size;y++)for(int x=0;x<size;x++)
        {
            var color=background;
            if((x-32)*(x-32)+(y-65)*(y-65)<29*29)color=shirt;
            if((x-32)*(x-32)+(y-27)*(y-27)<17*17)color=[252,223,196];
            if((x-32)*(x-32)+(y-24)*(y-24)<18*18&&y<22+(variant==2?Math.Abs(x-32)/3:0))color=variant==3?[122,78,49]:[47,58,80];
            if(y>=27&&y<=29&&(x>=25&&x<=27||x>=37&&x<=39))color=[47,58,80];
            if(y==36&&x>=29&&x<=35)color=[180,108,95];
            int position=y*(size*4+1)+1+x*4;pixels[position]=color[0];pixels[position+1]=color[1];pixels[position+2]=color[2];pixels[position+3]=255;
        }
        using var result=new MemoryStream();result.Write([137,80,78,71,13,10,26,10]);
        var header=new byte[13];BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0,4),size);BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4,4),size);header[8]=8;header[9]=6;
        Chunk(result,"IHDR",header);
        using var compressed=new MemoryStream();using(var zlib=new ZLibStream(compressed,CompressionLevel.Optimal,true))zlib.Write(pixels);
        Chunk(result,"IDAT",compressed.ToArray());Chunk(result,"IEND",[]);return result.ToArray();
    }
    private static void Chunk(Stream target,string type,byte[] data)
    {
        var name=Encoding.ASCII.GetBytes(type);Span<byte> value=stackalloc byte[4];BinaryPrimitives.WriteInt32BigEndian(value,data.Length);target.Write(value);target.Write(name);target.Write(data);
        uint crc=0xffffffff;foreach(var b in name.Concat(data)){crc^=b;for(int i=0;i<8;i++)crc=(crc&1)==1?(crc>>1)^0xedb88320:crc>>1;}
        BinaryPrimitives.WriteUInt32BigEndian(value,crc^0xffffffff);target.Write(value);
    }
}
