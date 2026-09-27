using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;

namespace PiWeb_Launcher.IconTool;

/// <summary>
/// 生成应用图标:使用 **pi.dev 官方 favicon 的标记**(https://pi.dev/favicon.svg)。
/// <para>
/// 为什么用官方标记而不是自己画一个:应用图标是"这是谁的外壳"的第一眼信息。
/// 自绘的 π 再好看也只是一个近似 —— 用户的 Dock/任务栏里应该出现和站点 tab 上一样的图形。
/// </para>
/// <para>
/// 官方 favicon 是三段**纯矩形组成的路径**(没有曲线、没有描边、不依赖字体),
/// 所以直接用 SkiaSharp 的 <see cref="SKPath.ParseSvgPathData"/> 解析即可,
/// 不需要为渲染一个图标引入完整的 SVG 渲染库。
/// </para>
/// <para>
/// 输出两份:<c>logo-512.png</c>(窗口/托盘图标,以 Avalonia 资源打进程序集)与
/// <c>logo.ico</c>(exe 的文件图标,编译期嵌入)。ICO 里放 **PNG 压缩的多个尺寸**
/// (256/128/64/48/32/16):Vista 之后的 Windows 都支持,而且比 BMP 小得多;
/// 而每个尺寸都是**独立渲染**的,不是把 256 缩下来 —— 小尺寸下留白要更少、
/// 标记要更满,缩出来的 16px 会糊成一团灰点。
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>
    /// 官方 favicon 的路径数据(原样抄自 https://pi.dev/favicon.svg,三段 <c>class="mark"</c>)。
    /// <para>
    /// ⚠ 抄的是**路径数据**而不是把 SVG 当资源读进来:官方 SVG 用 <c>@media (prefers-color-scheme)</c>
    /// 切黑/白两色,而 ICO 没有"跟随主题"这回事 —— 图标必须当场把颜色定死。
    /// 另外 <c>viewBox</c> 是 560×560,下面的坐标归一化依赖这个数字。
    /// </para>
    /// </summary>
    private static readonly string[] MarkPaths =
    [
        "M420 280H280V140H0V0H420V280Z",
        "M560 560H420V280H560V560Z",
        "M140 560H0V140H140V280H280V420H140V560Z",
    ];

    /// <summary>favicon 的 viewBox 边长(仅作参考说明;居中用路径包围盒,见 <see cref="Render"/>)。</summary>
    private const float MarkViewBox = 560f;

    /// <summary>
    /// 方块底色:官方 favicon 浅色主题下的页面底色 <c>#f6f6f6</c>(原值)。
    /// <para>
    /// ⚠ 不能用纯白:任务栏/资源管理器常是浅色背景,纯白方块与背景糊在一起,
    /// 图标就只剩一个悬浮的黑标记(实测踩到)。#f6f6f6 既与站点一致,又刚好能立住轮廓。
    /// </para>
    /// </summary>
    private static readonly SKColor Background = new(0xF6, 0xF6, 0xF6);

    /// <summary>标记颜色:官方 favicon 浅色主题下的 <c>#111111</c>(原值,不改)。</summary>
    private static readonly SKColor MarkColor = new(0x11, 0x11, 0x11);

    /// <summary>圆角方块的圆角比例(与 Fluent 应用图标的手感一致)。</summary>
    private const float CornerRadiusRatio = 0.22f;

    /// <summary>方块相对画布的内缩比例(留一点透明边,使方块轮廓在深浅两种背景上都读得出来)。</summary>
    private const float TileInsetRatio = 0.035f;

    /// <summary>
    /// 标记在方块内的占比。
    /// <para>
    /// 官方 favicon 是"满画布"的(标记顶到 viewBox 的边上),直接照搬会顶到圆角上,
    /// 因此这里留边。0.70 是实测下来"任务栏 16px 下仍看得清、又不显得空"的比例。
    /// </para>
    /// </summary>
    private const float MarkScaleRatio = 0.70f;

    /// <summary>ICO 里包含的尺寸(必须含 256 —— 资源管理器的大图标视图用的就是它)。</summary>
    private static readonly int[] IcoSizes = [256, 128, 64, 48, 32, 16];

    /// <summary>标记的路径(解析一次,各尺寸复用)。</summary>
    private static readonly SKPath Mark = BuildMarkPath();

    private static int Main(string[] args)
    {
        var outputDirectory = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PiWeb Launcher", "Assets");

        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var pngPath = Path.Combine(outputDirectory, "logo-512.png");
        var icoPath = Path.Combine(outputDirectory, "logo.ico");

        using (var bitmap = Render(512))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = File.Create(pngPath))
        {
            data.SaveTo(stream);
        }

        using (var ico = File.Create(icoPath))
        {
            WriteIco(ico, IcoSizes);
        }

        Console.WriteLine($"已生成 {pngPath}({new FileInfo(pngPath).Length} 字节)");
        Console.WriteLine($"已生成 {icoPath}({new FileInfo(icoPath).Length} 字节)");
        return 0;
    }

    /// <summary>把三段官方路径解析成一个 <see cref="SKPath"/>。</summary>
    private static SKPath BuildMarkPath()
    {
        var path = new SKPath();
        foreach (var data in MarkPaths)
        {
            using var segment = SKPath.ParseSvgPathData(data)
                ?? throw new InvalidOperationException("无法解析官方 favicon 的路径数据: " + data);
            path.AddPath(segment);
        }

        return path;
    }

    /// <summary>画一张 <paramref name="size"/>×<paramref name="size"/> 的图标。</summary>
    private static SKBitmap Render(int size)
    {
        var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(SKColors.Transparent);

        // 圆角方块
        var inset = size * TileInsetRatio;
        var rect = new SKRect(inset, inset, size - inset, size - inset);
        var radius = size * CornerRadiusRatio;

        using (var paint = new SKPaint { Color = Background, IsAntialias = true })
        {
            canvas.DrawRoundRect(rect, radius, radius, paint);
        }

        // 标记:按方块边长的固定比例居中摆放。
        // ⚠ 必须用**路径自己的包围盒**来居中,不能用 viewBox(560×560):
        // 官方 favicon 的 viewBox 与真正画出来的区域并不一致 —— 第三段路径里
        // "H140V560"(x=0..140、y=420..560)只是一条留空的笔画,标记的实际范围是 560×420。
        // 照 viewBox 居中会让标记明显偏左上(实测踩到)。
        // ⚠ 同样用 canvas 变换而不是改写路径坐标:路径是官方数据,原样保留才好在升级时逐字比对。
        var bounds = Mark.Bounds;
        var scale = rect.Width * MarkScaleRatio / Math.Max(bounds.Width, bounds.Height);

        canvas.Save();
        canvas.Translate(rect.MidX, rect.MidY);
        canvas.Scale(scale, scale);
        canvas.Translate(-bounds.MidX, -bounds.MidY);

        using (var paint = new SKPaint { Color = MarkColor, IsAntialias = true, Style = SKPaintStyle.Fill })
        {
            canvas.DrawPath(Mark, paint);
        }

        canvas.Restore();

        return bitmap;
    }

    /// <summary>
    /// 写 ICO 文件。结构:6 字节头 + 每张图 16 字节目录项 + 各图的 PNG 数据。
    /// </summary>
    private static void WriteIco(Stream stream, IReadOnlyList<int> sizes)
    {
        var images = new List<byte[]>(sizes.Count);
        foreach (var size in sizes)
        {
            using var bitmap = Render(size);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            images.Add(data.ToArray());
        }

        using var writer = new BinaryWriter(stream);

        // ICONDIR
        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // type = icon
        writer.Write((ushort)sizes.Count);    // image count

        // 目录项之后就是图像数据,偏移从整个目录之后开始算
        var offset = 6 + (16 * sizes.Count);
        for (var i = 0; i < sizes.Count; i++)
        {
            var size = sizes[i];

            // 256 在 ICO 目录里用 0 表示(一个字节放不下 256)
            writer.Write((byte)(size >= 256 ? 0 : size));   // width
            writer.Write((byte)(size >= 256 ? 0 : size));   // height
            writer.Write((byte)0);                          // 调色板颜色数(真彩为 0)
            writer.Write((byte)0);                          // reserved
            writer.Write((ushort)1);                        // color planes
            writer.Write((ushort)32);                       // bits per pixel
            writer.Write(images[i].Length);                 // 数据长度
            writer.Write(offset);                           // 数据偏移
            offset += images[i].Length;
        }

        foreach (var image in images)
        {
            writer.Write(image);
        }
    }
}
