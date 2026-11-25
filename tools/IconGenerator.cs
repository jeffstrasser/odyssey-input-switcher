using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length != 1)
    throw new ArgumentException("Supply the output .ico path.");
int[] sizes = [16, 32, 48, 64, 128, 256];
var images = new List<byte[]>();
foreach (int size in sizes)
{
    using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    graphics.ScaleTransform(size / 256f, size / 256f);
    graphics.Clear(Color.Transparent);
    using var bezel = new SolidBrush(Color.FromArgb(34, 46, 65));
    using var stand = new SolidBrush(Color.FromArgb(86, 107, 132));
    using var screen = new LinearGradientBrush(new Rectangle(27, 42, 202, 130),
        Color.FromArgb(38, 169, 222), Color.FromArgb(37, 70, 166), 45f);
    graphics.FillRectangle(stand, 111, 173, 34, 39);
    graphics.FillRectangle(stand, 73, 207, 110, 14);
    graphics.FillRectangle(bezel, 14, 29, 228, 157);
    graphics.FillRectangle(screen, 26, 41, 204, 130);
    using var light = new SolidBrush(Color.FromArgb(126, 231, 193));
    graphics.FillEllipse(light, 122, 176, 10, 5);
    using var stream = new MemoryStream();
    bitmap.Save(stream, ImageFormat.Png);
    images.Add(stream.ToArray());
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
using var writer = new BinaryWriter(File.Create(args[0]));
writer.Write((ushort)0);
writer.Write((ushort)1);
writer.Write((ushort)sizes.Length);
uint offset = (uint)(6 + sizes.Length * 16);
for (int i = 0; i < sizes.Length; i++)
{
    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write((uint)images[i].Length);
    writer.Write(offset);
    offset += (uint)images[i].Length;
}
foreach (byte[] image in images)
    writer.Write(image);
Console.WriteLine($"Created monitor icon: {args[0]}");
