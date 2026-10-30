# Conversor JPG, PDF, HEIC, TIFF

App Android que converte e comprime arquivos direto no celular, sem internet e sem enviar nada para servidor nenhum.

| De | Para |
|---|---|
| **PDF** | **Word (DOCX)**, texto (TXT), JPG ou PNG (uma imagem por página) |
| **Imagens**: JPG, PNG, WebP, HEIC, AVIF*, GIF, BMP, TIFF | PNG, JPG, WebP, TIFF e PDF (várias imagens viram um PDF só) |

**Comprimir** (três níveis: leve, média, forte):

- **PDF → PDF menor**, com o compressor do Filee adaptado para o Android: as imagens dentro do PDF são reduzidas e
  regravadas em JPEG; texto, fontes e desenhos ficam exatamente iguais.
- **Fotos → JPG menor** (PNG, quando a imagem tem transparência), com o lado maior limitado a 4096, 2560 ou 1600 pixels.
- Se o arquivo já estava otimizado, sai uma cópia do original e o app avisa.

\* AVIF abre a partir do Android 12. O Android não grava AVIF, por isso ele aparece só como origem.

A conversão de **PDF para Word e para texto usa o motor do [Filee](https://github.com/WDHUS4G/Filee)** (código aberto, licença MIT), sem nenhuma alteração: o mesmo código do Filee para computador, que lê o PDF com o PdfPig, remonta parágrafos, títulos, tabelas e imagens na ordem de leitura e grava o DOCX com o gerador próprio do Filee. Páginas digitalizadas (só imagem) entram no Word como imagem da página.

Os arquivos convertidos ficam em **Downloads › Conversor**. Também dá para mandar arquivos para o app pelo botão **Compartilhar** de outros apps (WhatsApp, Galeria, Arquivos, e-mail).

## Instalar no celular

1. No celular, abra este repositório no navegador (logado no GitHub) e entre em **Releases**.
2. Baixe o arquivo `Conversor-….apk` da versão mais recente.
3. Abra o arquivo baixado. Na primeira vez, o Android pede para permitir instalar apps "desta fonte" (o navegador ou o app de arquivos): permita e volte.
4. Toque em **Instalar**.

Precisa de Android 10 ou mais novo. As próximas versões instalam por cima da anterior (todas são assinadas com a mesma chave).

## Como o APK é gerado

Não precisa instalar nada no computador. A cada envio para a branch `main`, o **GitHub Actions** ([.github/workflows/apk.yml](.github/workflows/apk.yml)):

1. baixa o código e o Filee (submódulo em `external/Filee`);
2. instala o .NET 10 e o suporte a Android;
3. roda os testes do núcleo (`tests/Conversor.Core.Checks`);
4. compila e assina o APK;
5. **autoteste**: liga um Android 13 emulado, gera PDFs e imagens de exemplo dentro dele e confere cada conversão
   (PDF → Word, texto, JPG e PNG; imagens → PNG, WebP, TIFF e PDF; salvar em Downloads). O relatório, com capturas de
   tela, fica na branch [`autoteste`](../../tree/autoteste);
6. só se tudo passar, publica o APK em **Releases**.

Mudanças só em arquivos `.md` (como este) não geram APK novo.

Para gerar de novo sem mudar código: aba **Actions** › **Compilar APK** › **Run workflow**.

## Organização do código

```
src/Conversor.App/        o app Android (C#, .NET para Android)
  MainActivity.cs           a tela: escolher arquivos, formato, converter, abrir/compartilhar
  Conversion/               as conversões
    FileeEngine.cs            PDF → DOCX / TXT com o motor do Filee
    PdfPages.cs               PDF → JPG / PNG (leitor de PDF do Android)
    PdfBuilder.cs             imagens → PDF (PDFsharp, a mesma biblioteca do Filee)
    ImageConverter.cs         imagem → PNG / JPG / WebP / TIFF
    Downloads.cs              salva em Downloads/Conversor
  Compression/              comprimir
    Compressor.cs             PDF e fotos, nos três níveis
    PdfCompressor.cs          compressor de PDF do Filee, adaptado (Bitmap do Android no lugar do ImageMagick)
    PdfPictureUses.cs         copiado do Filee: onde e de que tamanho cada imagem aparece no PDF
  Ui/Kit.cs                 cores (claro/escuro) e componentes da tela
src/Conversor.Core/       parte sem Android, testada em qualquer computador
  FileFormats.cs            reconhece o formato pelo conteúdo do arquivo
  Targets.cs                quais formatos de destino aparecem para cada arquivo
  CompressionLevel.cs       os níveis de compressão (leve, média, forte)
  Tiff/                     leitor e gravador de TIFF (o Android não tem)
tests/Conversor.Core.Checks/  testes do núcleo
external/Filee/           o Filee (submódulo git, numa versão fixa)
```

### Rodar os testes no computador

Com o [.NET 10 SDK](https://dotnet.microsoft.com/download) instalado:

```
dotnet run --project tests/Conversor.Core.Checks
```

Para conferir o leitor de TIFF contra arquivos gravados por outros programas (Pillow e tifffile):

```
pip install pillow tifffile imagecodecs numpy
python tests/tools/make_tiff_samples.py amostras
dotnet run --project tests/Conversor.Core.Checks -- amostras
```

### Atualizar o Filee

```
cd external/Filee
git fetch origin
git checkout origin/main
cd ../..
git commit -am "Atualiza o Filee"
git push
```

### Nova versão do app

Em `src/Conversor.App/Conversor.App.csproj`, aumente `ApplicationDisplayVersion` (ex.: 1.1) e `ApplicationVersion` (ex.: 2; esse número precisa sempre crescer) e envie para a `main`.

## Limites conhecidos

- PDF com senha: precisa salvar uma cópia sem senha antes.
- PDF digitalizado → texto: não há reconhecimento de texto (OCR) no celular; use PDF → Word, que mantém as páginas como imagem.
- TIFF com compressão JPEG ou de fax (CCITT) ainda não abre (os mais comuns, sem compressão, LZW, Deflate e PackBits, abrem).

## Créditos

- [Filee](https://github.com/WDHUS4G/Filee), © Myeonggu Kim (KnifeLemon) e colaboradores, licença MIT.
- [PdfPig](https://github.com/UglyToad/PdfPig) (Apache 2.0), [PDFsharp](https://github.com/empira/PDFsharp) (MIT), [PDFtoImage](https://github.com/sungaila/PDFtoImage) (MIT), [SkiaSharp](https://github.com/mono/SkiaSharp) (MIT), usados pelo Filee.

A chave de assinatura (`src/Conversor.App/conversor.keystore`) está no repositório para o GitHub conseguir assinar o APK. Mantenha o repositório **privado**. Se um dia for publicar na Play Store, gere uma chave nova e guarde a senha nos Secrets do GitHub.
