using System.Globalization;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Conversor.App.Conversion;
using Conversor.App.Ui;
using Conversor.Core;
using AUri = Android.Net.Uri;

namespace Conversor.App;

/// <summary>
/// A única tela do app: escolher arquivos (ou recebê-los pelo "Compartilhar" de outro app), escolher o formato,
/// converter e abrir/compartilhar o resultado, que fica salvo em Downloads/Conversor.
/// </summary>
[Activity(
    Name = "com.gabrielwlopes.conversor.MainActivity",
    Label = "@string/app_name",
    MainLauncher = true,
    Exported = true,
    Theme = "@style/AppTheme",
    LaunchMode = LaunchMode.SingleTop,
    // A tela não é recriada ao girar o celular, assim uma conversão em andamento continua.
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
                           | ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode | ConfigChanges.KeyboardHidden)]
// O app aparece no menu "Compartilhar" de outros apps (WhatsApp, Galeria, Arquivos...) para PDFs e imagens.
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault },
    DataMimeTypes = new[] { "application/pdf", "image/*" })]
[IntentFilter(new[] { Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault },
    DataMimeTypes = new[] { "application/pdf", "image/*" })]
public class MainActivity : Activity
{
    private const int PickRequest = 1;

    private readonly List<PickedFile> _files = [];
    private FileFormat? _target;
    private bool _busy;
    private bool _pickAppends;
    private CancellationTokenSource? _cancel;

    private Kit _kit = null!;
    private ScrollView _scroll = null!;
    private LinearLayout _filesCard = null!, _filesBox = null!, _targetsCard = null!, _targetsBox = null!;
    private LinearLayout _progressCard = null!, _resultCard = null!;
    private TextView _convertButton = null!, _progressText = null!, _cancelButton = null!;
    private ProgressBar _progressBar = null!;

    private string InputFolder => Path.Combine(CacheDir!.AbsolutePath, "entrada");
    private string OutputFolder => Path.Combine(CacheDir!.AbsolutePath, "saida");

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _kit = new Kit(this, Palette.For(this));
        if (savedInstanceState is null)
        {
            // Sobras de uma sessão anterior.
            TryDelete(InputFolder);
            TryDelete(OutputFolder);
        }
        SetContentView(BuildScreen());
        Render();
#if SELFTEST
        if (savedInstanceState is null && Intent?.GetBooleanExtra("selftest", false) == true)
        {
            _ = RunSelfTestAsync();
            return;
        }
#endif
        if (savedInstanceState is null)
            HandleIncoming(Intent);
    }

#if SELFTEST
    /// <summary>Só no APK de teste: converte arquivos de exemplo e mostra o relatório na tela.</summary>
    private async Task RunSelfTestAsync()
    {
        SetBusy(true);
        ShowProgress("Autoteste: convertendo arquivos de exemplo…", indeterminate: true, cancellable: false);
        var (lines, passed, samples) = await Task.Run(() => SelfTest.RunAsync(this));
        HideProgress();
        SetBusy(false);
        _files.AddRange(samples);
        Render();
        ShowNotice((passed ? "Autoteste OK" : "Autoteste com falhas") + "\n\n" + string.Join("\n", lines), error: !passed);
    }
#endif

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleIncoming(intent);
    }

    protected override void OnDestroy()
    {
        _cancel?.Cancel();
        base.OnDestroy();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickRequest || resultCode != Result.Ok || data is null)
            return;
        var uris = UrisFrom(data);
        if (uris.Count > 0)
            _ = ImportAsync(uris, replace: !_pickAppends);
    }

    // ───────────────────────── Montagem da tela ─────────────────────────

    private View BuildScreen()
    {
        var colors = _kit.Colors;
        var root = new FrameLayout(this);
        root.SetBackgroundColor(colors.Background);

        _scroll = new ScrollView(this) { FillViewport = true };
        var page = _kit.Column();
        _scroll.AddView(page, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(_scroll, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // Topo roxo com o nome do app.
        var header = _kit.Column();
        header.SetBackgroundColor(colors.Header);
        var headerTop = _kit.Dp(28);
        header.SetPadding(_kit.Dp(22), headerTop, _kit.Dp(22), _kit.Dp(26));
        header.AddView(_kit.Text("Conversor", 30, Android.Graphics.Color.White, bold: true));
        var tagline = _kit.Text("PDF para Word e imagens para PNG, JPG, WebP, TIFF ou PDF. Tudo no celular, sem internet.",
            14, Android.Graphics.Color.White);
        tagline.Alpha = 0.85f;
        header.AddView(tagline, Kit.Fill(_kit.Dp(6)));
        page.AddView(header, Kit.Fill());

        var body = _kit.Column();
        var bodyBottom = _kit.Dp(28);
        body.SetPadding(_kit.Dp(16), _kit.Dp(16), _kit.Dp(16), bodyBottom);
        page.AddView(body, Kit.Fill());

        // 1. Arquivos
        _filesCard = _kit.Card();
        _filesCard.AddView(_kit.SectionTitle("1 · Arquivos"));
        _filesBox = _kit.Column();
        _filesCard.AddView(_filesBox, Kit.Fill(_kit.Dp(12)));
        body.AddView(_filesCard, Kit.Fill());

        // 2. Formato de destino
        _targetsCard = _kit.Card();
        _targetsCard.AddView(_kit.SectionTitle("2 · Converter para"));
        _targetsBox = _kit.Column();
        _targetsCard.AddView(_targetsBox, Kit.Fill(_kit.Dp(12)));
        body.AddView(_targetsCard, Kit.Fill(_kit.Dp(12)));

        _convertButton = _kit.PrimaryButton("Converter");
        _convertButton.Click += (_, _) => StartConversion();
        body.AddView(_convertButton, Kit.Fill(_kit.Dp(16)));

        // Andamento
        _progressCard = _kit.Card();
        _progressText = _kit.Text("", 14, colors.Text);
        _progressCard.AddView(_progressText);
        _progressBar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 1000 };
        _progressBar.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(colors.Accent);
        _progressBar.IndeterminateTintList = Android.Content.Res.ColorStateList.ValueOf(colors.Accent);
        _progressCard.AddView(_progressBar, Kit.Fill(_kit.Dp(10)));
        _cancelButton = _kit.SecondaryButton("Cancelar");
        _cancelButton.Click += (_, _) =>
        {
            _cancel?.Cancel();
            _progressText.Text = "Cancelando…";
        };
        _progressCard.AddView(_cancelButton, Kit.Wrap(topMarginPx: _kit.Dp(12)));
        _progressCard.Visibility = ViewStates.Gone;
        body.AddView(_progressCard, Kit.Fill(_kit.Dp(16)));

        // Resultado (ou avisos)
        _resultCard = _kit.Card();
        _resultCard.Visibility = ViewStates.Gone;
        body.AddView(_resultCard, Kit.Fill(_kit.Dp(16)));

        var footer = _kit.Text(
            "PDF para Word e para texto: motor do Filee (código aberto, licença MIT). Imagens: codificadores do Android. " +
            "Os arquivos convertidos ficam em Downloads › Conversor. Nada sai do seu celular.", 12, colors.Muted);
        body.AddView(footer, Kit.Fill(_kit.Dp(20)));

        // Android 15+: a tela vai até as bordas; o topo e o fim ganham o espaço das barras do sistema.
        if ((int)Build.VERSION.SdkInt >= 35)
            root.SetOnApplyWindowInsetsListener(new SystemBarsPadding(header, headerTop, body, bodyBottom));

        return root;
    }

    // ───────────────────────── Atualizar a tela ─────────────────────────

    private void Render()
    {
        RenderFiles();
        RenderTargets();
        RenderConvertButton();
    }

    private void RenderFiles()
    {
        var colors = _kit.Colors;
        _filesBox.RemoveAllViews();
        if (_files.Count == 0)
        {
            _filesBox.AddView(PickZone(), Kit.Fill());
            return;
        }

        foreach (var file in _files.ToList())
            _filesBox.AddView(FileRow(file), Kit.Fill());

        var actions = _kit.Row();
        var add = _kit.SecondaryButton("+ Adicionar");
        add.Click += (_, _) => Pick(append: true);
        actions.AddView(add, Kit.Wrap());
        var clear = _kit.Text("Limpar", 15, colors.Muted, bold: true);
        clear.SetPadding(_kit.Dp(16), _kit.Dp(11), _kit.Dp(16), _kit.Dp(11));
        clear.Click += (_, _) =>
        {
            if (_busy)
                return;
            foreach (var file in _files)
                TryDelete(file.LocalPath);
            _files.Clear();
            HideResult();
            Render();
        };
        actions.AddView(clear, Kit.Wrap(_kit.Dp(4)));
        _filesBox.AddView(actions, Kit.Fill(_kit.Dp(10)));
    }

    private View PickZone()
    {
        var colors = _kit.Colors;
        var zone = _kit.Column(22);
        zone.SetGravity(GravityFlags.Center);
        zone.Background = _kit.Pressable(colors.AccentSoft, 16, colors.Accent, 1.5f, dashed: true);
        zone.Clickable = true;
        zone.Focusable = true;

        var plus = _kit.Text("+", 32, colors.Accent, bold: true);
        plus.Gravity = GravityFlags.Center;
        zone.AddView(plus, Kit.Fill());
        var title = _kit.Text("Escolher arquivos", 17, colors.Accent, bold: true);
        title.Gravity = GravityFlags.Center;
        zone.AddView(title, Kit.Fill());
        var hint = _kit.Text("PDF, JPG, PNG, WebP, HEIC, AVIF ou TIFF.\nTambém dá para usar o “Compartilhar” de outro app.",
            13, colors.Muted);
        hint.Gravity = GravityFlags.Center;
        zone.AddView(hint, Kit.Fill(_kit.Dp(6)));

        zone.Click += (_, _) => Pick(append: false);
        return zone;
    }

    private View FileRow(PickedFile file)
    {
        var colors = _kit.Colors;
        var row = _kit.Row();
        row.SetPadding(0, _kit.Dp(7), 0, _kit.Dp(7));

        var badge = _kit.Text(file.Format.Label, 11, colors.Accent, bold: true);
        badge.Gravity = GravityFlags.Center;
        badge.SetPadding(_kit.Dp(4), _kit.Dp(6), _kit.Dp(4), _kit.Dp(6));
        badge.Background = _kit.Shape(colors.AccentSoft, 10);
        row.AddView(badge, new LinearLayout.LayoutParams(_kit.Dp(54), ViewGroup.LayoutParams.WrapContent));

        var texts = _kit.Column();
        texts.AddView(_kit.SingleLine(file.Name, 15, colors.Text), Kit.Fill());
        texts.AddView(_kit.Text(SizeLabel(file.Size), 12, colors.Muted), Kit.Fill());
        row.AddView(texts, Kit.Weighted(1, _kit.Dp(12)));

        var remove = _kit.Text("✕", 16, colors.Muted);
        remove.SetPadding(_kit.Dp(14), _kit.Dp(8), _kit.Dp(6), _kit.Dp(8));
        remove.ContentDescription = "Remover " + file.Name;
        remove.Click += (_, _) =>
        {
            if (_busy)
                return;
            _files.Remove(file);
            TryDelete(file.LocalPath);
            HideResult();
            Render();
        };
        row.AddView(remove, Kit.Wrap());
        return row;
    }

    private void RenderTargets()
    {
        var colors = _kit.Colors;
        _targetsBox.RemoveAllViews();
        if (_files.Count == 0)
        {
            _targetsBox.AddView(_kit.Text("Escolha um arquivo para ver os formatos disponíveis.", 14, colors.Muted));
            _target = null;
            return;
        }

        var options = Targets.For(_files.Select(f => f.Format).ToList());
        if (options.Count == 0)
        {
            _targetsBox.AddView(_kit.Text(
                "PDFs e imagens não podem ser convertidos juntos. Deixe só PDFs ou só imagens na lista.", 14, colors.Error));
            _target = null;
            return;
        }
        if (_target is null || options.All(o => o.Format != _target))
            _target = options[0].Format;

        for (var i = 0; i < options.Count; i += 2)
        {
            var row = _kit.Row();
            row.AddView(TargetChip(options[i]), Kit.Weighted(1));
            if (i + 1 < options.Count)
                row.AddView(TargetChip(options[i + 1]), Kit.Weighted(1, _kit.Dp(10)));
            else
                row.AddView(new View(this), Kit.Weighted(1, _kit.Dp(10)));
            _targetsBox.AddView(row, Kit.Fill(i == 0 ? 0 : _kit.Dp(10)));
        }
    }

    private View TargetChip(TargetOption option)
    {
        var colors = _kit.Colors;
        var selected = option.Format == _target;
        var chip = _kit.Column(12);
        chip.Background = selected
            ? _kit.Pressable(colors.AccentSoft, 14, colors.Accent, 2)
            : _kit.Pressable(colors.Surface, 14, colors.Border);
        chip.Clickable = true;
        chip.Focusable = true;
        chip.AddView(_kit.Text(option.Format.Label, 17, selected ? colors.Accent : colors.Text, bold: true));
        chip.AddView(_kit.Text(option.Description, 12, colors.Muted), Kit.Fill(_kit.Dp(2)));
        chip.Click += (_, _) =>
        {
            if (_busy)
                return;
            _target = option.Format;
            RenderTargets();
            RenderConvertButton();
        };
        return chip;
    }

    private void RenderConvertButton()
    {
        var ready = !_busy && _files.Count > 0 && _target is not null;
        _convertButton.Text = _target is null ? "Converter" : $"Converter para {_target.Label}";
        _convertButton.Enabled = ready;
        _convertButton.Alpha = ready ? 1f : 0.45f;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _filesCard.Alpha = busy ? 0.55f : 1f;
        _targetsCard.Alpha = busy ? 0.55f : 1f;
        RenderConvertButton();
    }

    private void ShowProgress(string message, bool indeterminate, bool cancellable)
    {
        _progressText.Text = message;
        _progressBar.Indeterminate = indeterminate;
        _progressBar.Progress = 0;
        _cancelButton.Visibility = cancellable ? ViewStates.Visible : ViewStates.Gone;
        _progressCard.Visibility = ViewStates.Visible;
    }

    private void HideProgress() => _progressCard.Visibility = ViewStates.Gone;

    private void HideResult()
    {
        _resultCard.RemoveAllViews();
        _resultCard.Visibility = ViewStates.Gone;
    }

    /// <summary>Um aviso no cartão de resultado (erro em vermelho, informação em cinza).</summary>
    private void ShowNotice(string message, bool error)
    {
        _resultCard.RemoveAllViews();
        _resultCard.AddView(_kit.Text(message, 14, error ? _kit.Colors.Error : _kit.Colors.Text));
        _resultCard.Visibility = ViewStates.Visible;
        ScrollToEnd();
    }

    private void ShowResult(IReadOnlyList<SavedFile> saved, IReadOnlyList<string> errors)
    {
        var colors = _kit.Colors;
        _resultCard.RemoveAllViews();

        if (saved.Count > 0)
        {
            _resultCard.AddView(_kit.Text("✓  Pronto", 19, colors.Success, bold: true));
            var where = saved.Count == 1 ? "Salvo em Downloads › Conversor" : $"{saved.Count} arquivos salvos em Downloads › Conversor";
            _resultCard.AddView(_kit.Text(where, 14, colors.Muted), Kit.Fill(_kit.Dp(4)));

            const int shown = 6;
            foreach (var file in saved.Take(shown))
                _resultCard.AddView(_kit.SingleLine("• " + file.Name, 14, colors.Text), Kit.Fill(_kit.Dp(6)));
            if (saved.Count > shown)
                _resultCard.AddView(_kit.Text($"+ {saved.Count - shown} outros", 14, colors.Muted), Kit.Fill(_kit.Dp(6)));

            var actions = _kit.Row();
            if (saved.Count == 1)
            {
                var open = _kit.PrimaryButton("Abrir");
                open.SetPadding(_kit.Dp(22), _kit.Dp(11), _kit.Dp(22), _kit.Dp(11));
                open.Click += (_, _) => Open(saved[0]);
                actions.AddView(open, Kit.Wrap());
            }
            var share = _kit.SecondaryButton(saved.Count == 1 ? "Compartilhar" : "Compartilhar todos");
            share.Click += (_, _) => Share(saved);
            actions.AddView(share, Kit.Wrap(saved.Count == 1 ? _kit.Dp(10) : 0));
            _resultCard.AddView(actions, Kit.Fill(_kit.Dp(14)));
        }

        if (errors.Count > 0)
        {
            var title = saved.Count > 0 ? "Alguns arquivos não foram convertidos" : "Não foi possível converter";
            _resultCard.AddView(_kit.Text(title, 15, colors.Error, bold: true), Kit.Fill(saved.Count > 0 ? _kit.Dp(18) : 0));
            foreach (var error in errors)
                _resultCard.AddView(_kit.Text(error, 13, colors.Text), Kit.Fill(_kit.Dp(6)));
        }

        _resultCard.Visibility = ViewStates.Visible;
        ScrollToEnd();
    }

    private void ScrollToEnd() => _scroll.Post(() => _scroll.FullScroll(FocusSearchDirection.Down));

    // ───────────────────────── Escolher e receber arquivos ─────────────────────────

    private void Pick(bool append)
    {
        if (_busy)
            return;
        _pickAppends = append;
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraMimeTypes, new[] { "application/pdf", "image/*" });
        intent.PutExtra(Intent.ExtraAllowMultiple, true);
        try
        {
            StartActivityForResult(intent, PickRequest);
        }
        catch (ActivityNotFoundException)
        {
            ShowNotice("Não encontrei o seletor de arquivos do Android.", error: true);
        }
    }

    /// <summary>Arquivos enviados por outro app pelo menu "Compartilhar".</summary>
    private void HandleIncoming(Intent? intent)
    {
        if (intent is null || (intent.Action != Intent.ActionSend && intent.Action != Intent.ActionSendMultiple))
            return;
        var uris = new List<AUri>();
#pragma warning disable CA1422, CS0618 // as versões novas desses métodos só existem a partir do Android 13
        if (intent.Action == Intent.ActionSend && intent.GetParcelableExtra(Intent.ExtraStream) is AUri one)
            uris.Add(one);
        if (intent.Action == Intent.ActionSendMultiple && intent.GetParcelableArrayListExtra(Intent.ExtraStream) is { } many)
        {
            foreach (var item in many)
            {
                if (item is AUri uri)
                    uris.Add(uri);
            }
        }
#pragma warning restore CA1422, CS0618
        if (uris.Count == 0)
            uris.AddRange(UrisFrom(intent));
        intent.SetAction(Intent.ActionMain); // não processar de novo o mesmo envio
        if (uris.Count > 0)
            _ = ImportAsync(uris, replace: true);
    }

    private static List<AUri> UrisFrom(Intent intent)
    {
        var uris = new List<AUri>();
        if (intent.ClipData is { } clip)
        {
            for (var i = 0; i < clip.ItemCount; i++)
            {
                if (clip.GetItemAt(i)?.Uri is { } uri)
                    uris.Add(uri);
            }
        }
        if (uris.Count == 0 && intent.Data is { } single)
            uris.Add(single);
        return uris;
    }

    private async Task ImportAsync(IReadOnlyList<AUri> uris, bool replace)
    {
        if (_busy)
            return;
        HideResult();
        SetBusy(true);
        ShowProgress(uris.Count == 1 ? "Lendo o arquivo…" : $"Lendo {uris.Count} arquivos…", indeterminate: true, cancellable: false);

        var folder = InputFolder;
        var errors = new List<string>();
        var imported = await Task.Run(() =>
        {
            var list = new List<PickedFile>();
            foreach (var uri in uris)
            {
                try
                {
                    list.Add(FileImport.Import(this, uri, folder));
                }
                catch (Exception e)
                {
                    errors.Add(Errors.Describe(e));
                }
            }
            return list;
        });

        if (replace)
        {
            foreach (var file in _files)
                TryDelete(file.LocalPath);
            _files.Clear();
        }
        _files.AddRange(imported);
        HideProgress();
        SetBusy(false);
        Render();
        if (errors.Count > 0)
            ShowNotice(string.Join("\n", errors), error: true);
    }

    // ───────────────────────── Converter ─────────────────────────

    private async void StartConversion()
    {
        if (_busy || _target is null || _files.Count == 0)
            return;
        var target = _target;
        var inputs = _files.ToList();
        var work = Path.Combine(OutputFolder, Guid.NewGuid().ToString("N"));
        var cancel = _cancel = new CancellationTokenSource();

        HideResult();
        SetBusy(true);
        ShowProgress("Preparando…", indeterminate: false, cancellable: true);
        var progress = new Progress<JobProgress>(p =>
        {
            if (!_busy)
                return;
            _progressBar.Progress = (int)(p.Fraction * 1000);
            _progressText.Text = p.Message;
        });

        try
        {
            var result = await Task.Run(() => ConversionJob.RunAsync(inputs, target, work, progress, cancel.Token), cancel.Token);
            _progressText.Text = "Salvando em Downloads…";
            var saveErrors = new List<string>();
            var saved = await Task.Run(() =>
            {
                var list = new List<SavedFile>();
                foreach (var file in result.Files)
                {
                    try
                    {
                        list.Add(Downloads.Save(this, file));
                    }
                    catch (Exception e)
                    {
                        saveErrors.Add($"{file.Name}: {Errors.Describe(e)}");
                    }
                }
                return list;
            });
            HideProgress();
            ShowResult(saved, [.. result.Errors, .. saveErrors]);
        }
        catch (System.OperationCanceledException)
        {
            HideProgress();
            ShowNotice("Conversão cancelada.", error: false);
        }
        catch (Exception e)
        {
            HideProgress();
            ShowNotice(Errors.Describe(e), error: true);
        }
        finally
        {
            _cancel = null;
            cancel.Dispose();
            _ = Task.Run(() => TryDelete(work));
            SetBusy(false);
        }
    }

    // ───────────────────────── Abrir e compartilhar ─────────────────────────

    private void Open(SavedFile file)
    {
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(file.Uri, file.MimeType);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        try
        {
            StartActivity(Intent.CreateChooser(intent, "Abrir com"));
        }
        catch (ActivityNotFoundException)
        {
            Toast.MakeText(this, "Nenhum app instalado abre esse tipo de arquivo.", ToastLength.Long)?.Show();
        }
    }

    private void Share(IReadOnlyList<SavedFile> files)
    {
        Intent intent;
        if (files.Count == 1)
        {
            intent = new Intent(Intent.ActionSend);
            intent.PutExtra(Intent.ExtraStream, files[0].Uri);
        }
        else
        {
            intent = new Intent(Intent.ActionSendMultiple);
            intent.PutParcelableArrayListExtra(Intent.ExtraStream, files.Select(f => (IParcelable)f.Uri).ToList());
        }
        intent.SetType(files.Select(f => f.MimeType).Distinct().Count() == 1 ? files[0].MimeType : "*/*");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        try
        {
            StartActivity(Intent.CreateChooser(intent, "Compartilhar"));
        }
        catch (ActivityNotFoundException)
        {
            Toast.MakeText(this, "Nenhum app disponível para compartilhar.", ToastLength.Long)?.Show();
        }
    }

    // ───────────────────────── Utilidades ─────────────────────────

    private static string SizeLabel(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " MB",
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // Pasta temporária: se não der para apagar agora, o Android limpa depois.
        }
    }

    /// <summary>No Android 15+, afasta o conteúdo das barras de status e de navegação.</summary>
    private sealed class SystemBarsPadding(View header, int headerTop, View body, int bodyBottom)
        : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
        {
            var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
            header.SetPadding(header.PaddingLeft, headerTop + bars.Top, header.PaddingRight, header.PaddingBottom);
            body.SetPadding(body.PaddingLeft, body.PaddingTop, body.PaddingRight, bodyBottom + bars.Bottom);
            view.SetPadding(bars.Left, 0, bars.Right, 0);
            return insets;
        }
    }
}
