using System.IO;
using Avalonia.Media.Imaging;
using QRCoder;

namespace Singularity.Services.Karaoke.Party;

/// <summary>The phones' address as a QR code: black on white with a quiet zone, which every phone camera reads.</summary>
public static class PhoneQr
{
    public static Bitmap Render(string url, int pixelsPerModule = 12)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule);
        return new Bitmap(new MemoryStream(png));
    }
}
