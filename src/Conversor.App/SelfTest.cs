#if SELFTEST
// Autoteste: só entra no APK de teste (compilado com ConversorSelfTest=true no GitHub Actions), nunca no APK
// publicado. Gera arquivos de exemplo no próprio aparelho, roda cada conversão e confere o resultado.
// O resultado vai para o logcat (tag ConversorTest), que o GitHub Actions lê no emulador.

using System.IO.Compression;
using System.Text.RegularExpressions;
using Conversor.App.Compression;
using Android.Content;
using Conversor.App.Conversion;
using Conversor.Core;
using Conversor.Core.Tiff;
using Bitmap = Android.Graphics.Bitmap;
using CompressionLevel = Conversor.Core.CompressionLevel; // não confundir com o System.IO.Compression.CompressionLevel
using BitmapFactory = Android.Graphics.BitmapFactory;
using Canvas = Android.Graphics.Canvas;
using Color = Android.Graphics.Color;
using Paint = Android.Graphics.Paint;
using PaintFlags = Android.Graphics.PaintFlags;
using Rect = Android.Graphics.Rect;

namespace Conversor.App;

internal static class SelfTest
{
    public const string Tag = "ConversorTest";
    private const string Phrase = "teste do Conversor";

    /// <summary>Roda tudo; devolve as linhas do relatório ("OK ..." / "FALHOU ...") e os arquivos de exemplo.</summary>
    public static async Task<(List<string> Lines, bool Passed, List<PickedFile> Samples)> RunAsync(Context context)
    {
        var lines = new List<string>();
        var passed = true;
        var folder = Path.Combine(context.CacheDir!.AbsolutePath, "autoteste");
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);

        void Log(string line)
        {
            lines.Add(line);
            Android.Util.Log.Info(Tag, line);
        }

        async Task Step(string name, Func<Task<string>> body)
        {
            var started = DateTime.UtcNow;
            try
            {
                var detail = await body();
                Log($"OK {name}: {detail} ({(DateTime.UtcNow - started).TotalSeconds:0.0}s)");
            }
            catch (Exception e)
            {
                passed = false;
                Log($"FALHOU {name}: {e.GetType().Name}: {e.Message}");
                Android.Util.Log.Error(Tag, e.ToString());
            }
        }

        // Arquivos de exemplo
        var textPdf = Sample(folder, "Relatório de teste.pdf", FileFormats.Pdf, path => TextPdf(path));
        var scanPdf = Sample(folder, "Digitalizado.pdf", FileFormats.Pdf, path => ScannedPdf(path));
        var photo = Sample(folder, "Foto da viagem.jpg", FileFormats.Jpg, path => SaveBitmap(Photo(900, 600), path, Bitmap.CompressFormat.Jpeg!));
        var logo = Sample(folder, "Logo.png", FileFormats.Png, path => SaveBitmap(Logo(400), path, Bitmap.CompressFormat.Png!));
        var bigPhoto = Sample(folder, "Foto grande.jpg", FileFormats.Jpg, path => SaveBitmap(Photo(3000, 2000), path, Bitmap.CompressFormat.Jpeg!, 97));
        var tiff = Sample(folder, "Scanner.tiff", FileFormats.Tiff, path =>
        {
            var bitmap = Photo(640, 480);
            using var file = File.Create(path);
            TiffEncoder.Write(file, bitmap.Width, bitmap.Height, false,
                (y, row) => bitmap.GetPixels(row, 0, bitmap.Width, 0, y, bitmap.Width, 1));
            ImageCodec.Release(bitmap);
        });

        await Step("detecção de formato", () =>
        {
            foreach (var sample in new[] { textPdf, scanPdf, photo, logo, tiff })
            {
                var header = File.ReadAllBytes(sample.LocalPath).Take(FileFormats.HeaderLength).ToArray();
                Expect(FileFormats.Detect(header) == sample.Format, $"{sample.Name} reconhecido como {FileFormats.Detect(header)?.Id}");
            }
            return Task.FromResult("5 arquivos");
        });

        await Step("PDF → DOCX (motor do Filee)", async () =>
        {
            var file = await One(textPdf, FileFormats.Docx, folder);
            var text = DocxText(file.LocalPath);
            Expect(text.Contains(Phrase), "o DOCX não tem o texto do PDF: " + text[..Math.Min(120, text.Length)]);
            Expect(text.Contains("Segunda página"), "faltou a segunda página");
            return $"{new FileInfo(file.LocalPath).Length / 1024} KB, texto ok";
        });

        await Step("PDF digitalizado → DOCX (Filee + PDFium)", async () =>
        {
            var file = await One(scanPdf, FileFormats.Docx, folder);
            using var zip = ZipFile.OpenRead(file.LocalPath);
            var pictures = zip.Entries.Count(e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal));
            Expect(pictures >= 1, "a página digitalizada não virou imagem no DOCX");
            return $"{pictures} imagem(ns) no DOCX";
        });

        await Step("PDF → TXT (motor do Filee)", async () =>
        {
            var file = await One(textPdf, FileFormats.Txt, folder);
            var text = await File.ReadAllTextAsync(file.LocalPath);
            Expect(text.Contains(Phrase), "o TXT não tem o texto do PDF");
            return $"{text.Length} caracteres";
        });

        await Step("PDF → PNG (uma por página)", async () =>
        {
            var files = await Run([textPdf], FileFormats.Png, folder);
            Expect(files.Count == 2, $"esperava 2 imagens, vieram {files.Count}");
            var (w, h) = Size(files[0].LocalPath);
            Expect(w > 1000 && h > w, $"tamanho estranho {w}×{h}");
            return $"{files.Count} imagens, {w}×{h}, {string.Join(", ", files.Select(f => f.Name))}";
        });

        await Step("PDF → JPG", async () =>
        {
            var files = await Run([scanPdf], FileFormats.Jpg, folder);
            var (w, h) = Size(files[0].LocalPath);
            return $"{w}×{h}";
        });

        foreach (var target in new[] { FileFormats.Png, FileFormats.Webp, FileFormats.Tiff })
        {
            await Step($"JPG → {target.Label}", async () =>
            {
                var file = await One(photo, target, folder);
                Expect(file.Name == "Foto da viagem." + target.Extension, "nome de saída: " + file.Name);
                var (w, h) = target == FileFormats.Tiff
                    ? TiffSize(file.LocalPath)
                    : Size(file.LocalPath);
                Expect(w == 900 && h == 600, $"tamanho {w}×{h}");
                return $"{new FileInfo(file.LocalPath).Length / 1024} KB";
            });
        }

        await Step("PNG com transparência → JPG", async () =>
        {
            var file = await One(logo, FileFormats.Jpg, folder);
            var bitmap = BitmapFactory.DecodeFile(file.LocalPath)!;
            var corner = bitmap.GetPixel(2, 2);
            ImageCodec.Release(bitmap);
            Expect((corner & 0xFFFFFF) > 0xF0F0F0, $"o fundo transparente devia ficar branco (veio #{corner & 0xFFFFFF:X6})");
            return "fundo branco";
        });

        await Step("TIFF → PNG", async () =>
        {
            var file = await One(tiff, FileFormats.Png, folder);
            var (w, h) = Size(file.LocalPath);
            Expect(w == 640 && h == 480, $"tamanho {w}×{h}");
            return $"{w}×{h}";
        });

        await Step("imagens → um PDF", async () =>
        {
            var file = await One([photo, logo, tiff], FileFormats.Pdf, folder);
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(file.LocalPath);
            Expect(pdf.NumberOfPages == 3, $"esperava 3 páginas, vieram {pdf.NumberOfPages}");
            return $"{pdf.NumberOfPages} páginas, {new FileInfo(file.LocalPath).Length / 1024} KB";
        });

        await Step("comprimir PDF digitalizado (forte)", async () =>
        {
            var file = await Compress(scanPdf, CompressionLevel.Strong, folder);
            var after = new FileInfo(file.LocalPath).Length;
            Expect(!file.NotSmaller && after < scanPdf.Size, $"não diminuiu: {scanPdf.Size} → {after} bytes");
            Expect(file.Name == "Digitalizado_comprimido.pdf", "nome: " + file.Name);
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(file.LocalPath);
            Expect(pdf.NumberOfPages == 1, "páginas: " + pdf.NumberOfPages);
            return $"{scanPdf.Size / 1024} KB → {after / 1024} KB";
        });

        await Step("comprimir PDF de texto (o texto continua)", async () =>
        {
            var file = await Compress(textPdf, CompressionLevel.Balanced, folder);
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(file.LocalPath);
            Expect(pdf.NumberOfPages == 2, "páginas: " + pdf.NumberOfPages);
            var text = string.Concat(pdf.GetPage(1).Letters.Select(l => l.Value));
            Expect(text.Contains("Relat"), "o texto sumiu: " + text[..Math.Min(80, text.Length)]);
            return $"{textPdf.Size / 1024} KB → {new FileInfo(file.LocalPath).Length / 1024} KB{(file.NotSmaller ? " (já otimizado)" : "")}";
        });

        await Step("comprimir foto grande (média)", async () =>
        {
            var file = await Compress(bigPhoto, CompressionLevel.Balanced, folder);
            var (w, h) = Size(file.LocalPath);
            var after = new FileInfo(file.LocalPath).Length;
            Expect(w == 2560 && h == 1707, $"tamanho {w}×{h}");
            Expect(after < bigPhoto.Size && file.Format == FileFormats.Jpg, $"não diminuiu: {bigPhoto.Size} → {after}");
            return $"3000×2000 {bigPhoto.Size / 1024} KB → {w}×{h} {after / 1024} KB";
        });

        await Step("comprimir PNG com transparência", async () =>
        {
            var file = await Compress(logo, CompressionLevel.Strong, folder);
            Expect(file.Format == FileFormats.Png, "virou " + file.Format.Id);
            Expect(new FileInfo(file.LocalPath).Length <= logo.Size, "ficou maior que o original");
            return file.NotSmaller ? "já estava otimizado (cópia do original)" : $"{new FileInfo(file.LocalPath).Length / 1024} KB";
        });

        await Step("salvar em Downloads", () =>
        {
            var source = Path.Combine(folder, "salvar.txt");
            File.WriteAllText(source, "autoteste");
            var saved = Downloads.Save(context, new ConvertedFile(source, "autoteste-conversor.txt", FileFormats.Txt));
            Expect(saved.Size == 9, "tamanho salvo: " + saved.Size);
            var removed = context.ContentResolver!.Delete(saved.Uri, null, null);
            return Task.FromResult($"{saved.Name} ({saved.Uri}), removido: {removed}");
        });

        Log(passed ? "SELFTEST PASSED" : "SELFTEST FAILED");
        return (lines, passed, new List<PickedFile> { textPdf, scanPdf });
    }

    // ───────────── Conversões ─────────────

    private static Task<List<ConvertedFile>> Run(IReadOnlyList<PickedFile> inputs, FileFormat target, string folder) =>
        Run(inputs, new TargetOption(target, ""), CompressionLevel.Balanced, folder);

    private static async Task<ConvertedFile> Compress(PickedFile input, CompressionLevel level, string folder) =>
        (await Run([input], new TargetOption(input.Format, "", Compress: true), level, folder)).Single();

    private static async Task<List<ConvertedFile>> Run(IReadOnlyList<PickedFile> inputs, TargetOption target,
        CompressionLevel level, string folder)
    {
        var result = await ConversionJob.RunAsync(inputs, target, level, Path.Combine(folder, "saida-" + Guid.NewGuid().ToString("N")),
            new Progress<JobProgress>(), CancellationToken.None);
        if (result.Errors.Count > 0)
            throw new InvalidOperationException(string.Join(" | ", result.Errors));
        Expect(result.Files.Count > 0, "nenhum arquivo gerado");
        foreach (var file in result.Files)
            Expect(new FileInfo(file.LocalPath).Length > 0, file.Name + " vazio");
        return result.Files.ToList();
    }

    private static async Task<ConvertedFile> One(PickedFile input, FileFormat target, string folder) =>
        (await Run([input], target, folder)).Single();

    private static async Task<ConvertedFile> One(IReadOnlyList<PickedFile> inputs, FileFormat target, string folder) =>
        (await Run(inputs, target, folder)).Single();

    private static void Expect(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string DocxText(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("sem word/document.xml");
        using var reader = new StreamReader(entry.Open());
        var xml = reader.ReadToEnd();
        return Regex.Replace(Regex.Replace(xml, "</w:p>", "\n"), "<[^>]+>", "");
    }

    private static (int Width, int Height) Size(string path)
    {
        var options = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeFile(path, options);
        return (options.OutWidth, options.OutHeight);
    }

    private static (int Width, int Height) TiffSize(string path)
    {
        var page = TiffDecoder.Decode(File.ReadAllBytes(path));
        return (page.Width, page.Height);
    }

    // ───────────── Arquivos de exemplo ─────────────

    private static PickedFile Sample(string folder, string name, FileFormat format, Action<string> write)
    {
        var path = Path.Combine(folder, "entrada-" + name);
        write(path);
        return new PickedFile(path, name, new FileInfo(path).Length, format);
    }

    /// <summary>PDF com texto de verdade (duas páginas), feito com o gerador de PDF do Android.</summary>
    private static void TextPdf(string path)
    {
        var document = new Android.Graphics.Pdf.PdfDocument();
        try
        {
            var title = new Paint(PaintFlags.AntiAlias) { Color = Color.Black, TextSize = 26f };
            var body = new Paint(PaintFlags.AntiAlias) { Color = Color.DarkGray, TextSize = 13f };
            for (var number = 1; number <= 2; number++)
            {
                var info = new Android.Graphics.Pdf.PdfDocument.PageInfo.Builder(595, 842, number).Create()!;
                var page = document.StartPage(info)!;
                var canvas = page.Canvas!;
                if (number == 1)
                {
                    canvas.DrawText("Relatório de " + Phrase, 56, 90, title);
                    var y = 140f;
                    foreach (var line in new[]
                             {
                                 "Este PDF foi gerado no próprio aparelho para testar a conversão.",
                                 "Ele tem acentos (ação, coração, pão), números (1.234,56) e duas páginas.",
                                 "O motor do Filee deve transformar este texto em parágrafos do Word.",
                             })
                    {
                        canvas.DrawText(line, 56, y, body);
                        y += 22;
                    }
                }
                else
                {
                    canvas.DrawText("Segunda página", 56, 90, title);
                    canvas.DrawText("Fim do relatório.", 56, 140, body);
                }
                document.FinishPage(page);
            }
            using var file = File.Create(path);
            document.WriteTo(file);
        }
        finally
        {
            document.Close();
        }
    }

    /// <summary>PDF "digitalizado": a página é só uma imagem, sem texto (como um scan).</summary>
    private static void ScannedPdf(string path)
    {
        var scan = Bitmap.CreateBitmap(1240, 1754, Bitmap.Config.Argb8888!)!;
        using (var canvas = new Canvas(scan))
        {
            canvas.DrawColor(Color.White);
            var ink = new Paint(PaintFlags.AntiAlias) { Color = Color.Black, TextSize = 52f };
            canvas.DrawText("Documento digitalizado", 120, 220, ink);
            ink.TextSize = 30f;
            for (var i = 0; i < 20; i++)
                canvas.DrawText($"Linha {i + 1} de um papel que passou pelo scanner.", 120, 340 + i * 56, ink);
        }
        var document = new Android.Graphics.Pdf.PdfDocument();
        try
        {
            var info = new Android.Graphics.Pdf.PdfDocument.PageInfo.Builder(595, 842, 1).Create()!;
            var page = document.StartPage(info)!;
            page.Canvas!.DrawBitmap(scan, null, new Rect(0, 0, 595, 842), null);
            document.FinishPage(page);
            using var file = File.Create(path);
            document.WriteTo(file);
        }
        finally
        {
            document.Close();
            ImageCodec.Release(scan);
        }
    }

    /// <summary>Uma "foto": degradê com formas, sem transparência.</summary>
    private static Bitmap Photo(int width, int height)
    {
        var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bitmap);
        var paint = new Paint(PaintFlags.AntiAlias);
        for (var y = 0; y < height; y += 4)
        {
            paint.Color = new Color(30 + y * 120 / height, 90 + y * 100 / height, 200 - y * 120 / height);
            canvas.DrawRect(0, y, width, y + 4, paint);
        }
        paint.Color = new Color(250, 200, 60);
        canvas.DrawCircle(width * 0.75f, height * 0.3f, height * 0.15f, paint);
        paint.Color = new Color(40, 70, 40);
        canvas.DrawRect(0, height * 0.75f, width, height, paint);
        bitmap.HasAlpha = false;
        return bitmap;
    }

    /// <summary>Um logo: círculo colorido com fundo transparente.</summary>
    private static Bitmap Logo(int size)
    {
        var bitmap = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bitmap);
        var paint = new Paint(PaintFlags.AntiAlias) { Color = new Color(91, 79, 214) };
        canvas.DrawCircle(size / 2f, size / 2f, size * 0.35f, paint);
        return bitmap;
    }

    private static void SaveBitmap(Bitmap bitmap, string path, Bitmap.CompressFormat format, int quality = 92)
    {
        using (var file = File.Create(path))
            bitmap.Compress(format, quality, file);
        ImageCodec.Release(bitmap);
    }
}
#endif
