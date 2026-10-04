# Video Player

Windows 10/11 için Filmler ve TV tarzında, sade bir video oynatıcı. Altyapıda **mpv**
(varsayılan) veya **VLC** motoru çalışır, böylece her formatı oynatır ve Discord gibi
ekran paylaşımlarında takılma yapmaz.

## Özellikler

- Filmler ve TV'ye benzeyen kenarlıksız arayüz; kontroller ve pencere düğmeleri fare
  durunca kaybolur
- Açılan videonun klasöründeki diğer videolar Dosya Gezgini sırasıyla listeye girer
- **Tutup yana çekme:** video pencere içinde kayar, yeterince çekip bırakınca kayıp çıkar
  ve klasördeki sonraki/önceki video başlar
- **Hassas sarma:** çubuğun herhangi bir yerine basıp sürükleme; basılıyken video duraklar,
  fare nerede durursa o kare görünür. İmleci çubuktan yukarı kaldırdıkça 3 kata kadar
  daha ince ayar yapılır
- mpv motoru ekranın yenileme hızıyla senkron çizer (kayma 144 Hz ekranda akıcı)
- Tam ekran, ekranı kapla ve her zaman üstte kalan mini görünüm
- Oynatma hızı (0,5x – 2x), video bitince dur / sonrakine geç / tekrarla
- Pencere boyutu, konumu ve ses ayarları hatırlanır
- Tek pencere: oynatıcı açıkken başka bir videoya çift tıklamak aynı pencerede açar
- "Varsayılan video oynatıcısı yap" seçeneği

## Kısayollar

| Tuş | İşlev |
|---|---|
| Boşluk / K | Oynat / duraklat |
| ← / → | 10 sn geri / 30 sn ileri |
| ↑ / ↓ | Ses |
| M | Sesi kapat / aç |
| F / F11 / çift tık | Tam ekran (Esc ile çıkış) |
| N / P | Sonraki / önceki video |
| . / , | Hızlandır / yavaşlat |
| Ctrl+O | Dosya aç |
| Farenin yan tuşları | Önceki / sonraki video |

## Derleme

Gerekenler: .NET 8 SDK, 7-Zip.

```powershell
# mpv motoru için kütüphaneyi indir (bir kez)
.\tools\libmpv-indir.ps1

# Derle ve Uygulama klasörüne çıkar
dotnet publish -c Release -o Uygulama -p:DebugType=none
```

`libmpv-2.dll` indirilmezse uygulama VLC motoruyla çalışır. Motor, "⋯" menüsündeki
**Oynatma motoru** bölümünden değiştirilebilir.

## Kullanılan bileşenler

- [mpv](https://mpv.io) — libmpv, [shinchiro derlemesi](https://github.com/shinchiro/mpv-winbuild-cmake)
- [LibVLCSharp](https://github.com/videolan/libvlcsharp) ve VideoLAN.LibVLC.Windows
