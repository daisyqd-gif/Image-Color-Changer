using Microsoft.Win32;
using System;
using System.Collections.Generic;
using DrawingColor = System.Drawing.Color;
using Bitmap = System.Drawing.Bitmap;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RecolorWpf
{
    public partial class MainWindow : Window
    {
        const int CLUSTERS = 4;

        // Single-document for now, but structured to support multiple
        private ImageDocument _document;
        private ClusterViewModel[] _clusters;

        public MainWindow()
        {
            InitializeComponent();
        }

        // -----------------------------
        // LOAD IMAGE
        // -----------------------------
        private void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = "PNG Images|*.png"
            };
            if (ofd.ShowDialog() != true) return;

            LoadImageFromPath(ofd.FileName);
        }

        private void LoadImageFromPath(string path)
        {
            _document?.Dispose();

            var bmp = new Bitmap(path);
            _document = new ImageDocument(bmp);

            ImgOriginal.Source = BitmapToImageSource(_document.Original);
            ImgPreview.Source = null;

            RunClustering();

            BtnSave.IsEnabled = false;
        }

        // -----------------------------
        // CLUSTERING
        // -----------------------------
        private void RunClustering()
        {
            if (_document == null || _document.Original == null)
            {
                ClusterList.ItemsSource = null;
                return;
            }

            var bmp = _document.Original;
            var pixels = new List<PixelInfo>();

            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.A == 0) continue;

                    RGBtoHSL(c, out double h, out double s, out double l);
                    if (s < 0.1) continue;

                    pixels.Add(new PixelInfo { X = x, Y = y, H = h, S = s, L = l });
                }
            }

            if (pixels.Count == 0)
            {
                ClusterList.ItemsSource = null;
                _document.Pixels = null;
                _document.Assignments = null;
                return;
            }

            _document.Pixels = pixels;
            _document.Assignments = new int[pixels.Count];

            // K-means on hue
            double[] centers = new double[CLUSTERS];
            var rnd = new Random();
            for (int i = 0; i < CLUSTERS; i++)
                centers[i] = rnd.NextDouble() * 360;

            for (int iter = 0; iter < 10; iter++)
            {
                // assign
                for (int i = 0; i < pixels.Count; i++)
                {
                    double bestDist = double.MaxValue;
                    int best = 0;

                    for (int c = 0; c < CLUSTERS; c++)
                    {
                        double d = Math.Abs(pixels[i].H - centers[c]);
                        if (d > 180) d = 360 - d;
                        if (d < bestDist)
                        {
                            bestDist = d;
                            best = c;
                        }
                    }

                    _document.Assignments[i] = best;
                }

                // recompute centers
                double[] sum = new double[CLUSTERS];
                int[] count = new int[CLUSTERS];

                for (int i = 0; i < pixels.Count; i++)
                {
                    sum[_document.Assignments[i]] += pixels[i].H;
                    count[_document.Assignments[i]]++;
                }

                for (int c = 0; c < CLUSTERS; c++)
                {
                    if (count[c] > 0)
                        centers[c] = sum[c] / count[c];
                }
            }

            // Build cluster view models
            _clusters = new ClusterViewModel[CLUSTERS];

            for (int c = 0; c < CLUSTERS; c++)
            {
                double sumR = 0, sumG = 0, sumB = 0;
                int count = 0;

                for (int i = 0; i < pixels.Count; i++)
                {
                    if (_document.Assignments[i] == c)
                    {
                        var px = bmp.GetPixel(pixels[i].X, pixels[i].Y);
                        sumR += px.R;
                        sumG += px.G;
                        sumB += px.B;
                        count++;
                    }
                }

                System.Windows.Media.Color original;

                if (count > 0)
                {
                    int r = (int)(sumR / count);
                    int g = (int)(sumG / count);
                    int b = (int)(sumB / count);

                    original = System.Windows.Media.Color.FromRgb((byte)r, (byte)g, (byte)b);
                }
                else
                {
                    original = System.Windows.Media.Colors.Transparent;
                }

                var vm = new ClusterViewModel
                {
                    Index = c,
                    OriginalColor = original,
                    NewColor = original
                };

                vm.NewColorChanged += OnClusterColorChanged;
                _clusters[c] = vm;
            }

            ClusterList.ItemsSource = _clusters;
        }

        // -----------------------------
        // DYNAMIC RECOLOR
        // -----------------------------
        private void OnClusterColorChanged(ClusterViewModel changedCluster)
        {
            if (_document == null || _document.Pixels == null || _document.Assignments == null)
                return;

            _document.ResetWorking();
            RecolorEngine.Recolor(_document, _clusters);

            ImgPreview.Source = BitmapToImageSource(_document.Working);
            BtnSave.IsEnabled = true;
        }

        private void ClusterNewColor_Click(object sender, MouseButtonEventArgs e)
        {
            var rect = (System.Windows.Shapes.Rectangle)sender;
            var cluster = (ClusterViewModel)rect.DataContext;

            var dlg = new System.Windows.Forms.ColorDialog();
            dlg.Color = DrawingColor.FromArgb(cluster.NewColor.R, cluster.NewColor.G, cluster.NewColor.B);

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                cluster.NewColor = System.Windows.Media.Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
            }
        }

        // -----------------------------
        // SAVE
        // -----------------------------
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_document == null || _document.Working == null) return;

            var sfd = new SaveFileDialog
            {
                Filter = "PNG Image|*.png",
                FileName = "recolored.png"
            };
            if (sfd.ShowDialog() != true) return;

            _document.Working.Save(sfd.FileName, ImageFormat.Png);
        }

        // -----------------------------
        // DRAG & DROP
        // -----------------------------
        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;

            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length == 0)
                return;

            string path = files[0];
            if (!File.Exists(path))
                return;

            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Only PNG files are supported.");
                return;
            }

            LoadImageFromPath(path);
        }

        // -----------------------------
        // HELPERS
        // -----------------------------
        private static BitmapImage BitmapToImageSource(Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }

        public static void RGBtoHSL(DrawingColor c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0;
            double g = c.G / 255.0;
            double b = c.B / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));

            h = s = l = (max + min) / 2.0;

            if (max == min)
            {
                h = s = 0;
                return;
            }

            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;

            h *= 60;
        }

        public static DrawingColor HSLtoRGB(double h, double s, double l)
        {
            double C = (1 - Math.Abs(2 * l - 1)) * s;
            double X = C * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = l - C / 2;

            double r = 0, g = 0, b = 0;

            if (h < 60) { r = C; g = X; b = 0; }
            else if (h < 120) { r = X; g = C; b = 0; }
            else if (h < 180) { r = 0; g = C; b = X; }
            else if (h < 240) { r = 0; g = X; b = C; }
            else if (h < 300) { r = X; g = 0; b = C; }
            else { r = C; g = 0; b = X; }

            return DrawingColor.FromArgb(
                (int)((r + m) * 255),
                (int)((g + m) * 255),
                (int)((b + m) * 255)
            );
        }
    }

    // -----------------------------
    // DATA STRUCTURES
    // -----------------------------
    public struct PixelInfo
    {
        public int X;
        public int Y;
        public double H;
        public double S;
        public double L;
    }

    public class ImageDocument : IDisposable
    {
        public Bitmap Original { get; private set; }
        public Bitmap Working { get; private set; }

        public List<PixelInfo> Pixels { get; set; }
        public int[] Assignments { get; set; }

        public ImageDocument(Bitmap original)
        {
            Original = original;
            Working = (Bitmap)original.Clone();
        }

        public void ResetWorking()
        {
            Working?.Dispose();
            Working = (Bitmap)Original.Clone();
        }

        public void Dispose()
        {
            Original?.Dispose();
            Working?.Dispose();
        }
    }

    public class ClusterViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public int Index { get; set; }

        public System.Windows.Media.Color OriginalColor { get; set; }

        private System.Windows.Media.Color _newColor;
        public System.Windows.Media.Color NewColor
        {
            get => _newColor;
            set
            {
                _newColor = value;
                OnPropertyChanged(nameof(NewColor));
                OnPropertyChanged(nameof(NewBrush));
                NewColorChanged?.Invoke(this);
            }
        }

        public Brush OriginalBrush => new SolidColorBrush(OriginalColor);
        public Brush NewBrush => new SolidColorBrush(NewColor);

        public string Hex => $"#{OriginalColor.R:X2}{OriginalColor.G:X2}{OriginalColor.B:X2}";

        public event Action<ClusterViewModel> NewColorChanged;

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public static class RecolorEngine
    {
        public static void Recolor(ImageDocument doc, ClusterViewModel[] clusters)
        {
            if (doc.Pixels == null || doc.Assignments == null) return;

            // Precompute target hue per cluster
            double[] targetHues = new double[clusters.Length];
            bool[] hasCluster = new bool[clusters.Length];

            for (int i = 0; i < clusters.Length; i++)
            {
                var c = clusters[i].NewColor;
                var sys = DrawingColor.FromArgb(c.R, c.G, c.B);
                MainWindow.RGBtoHSL(sys, out double h, out _, out _);
                targetHues[i] = h;
                hasCluster[i] = true;
            }

            for (int i = 0; i < doc.Pixels.Count; i++)
            {
                int clusterIndex = doc.Assignments[i];
                if (!hasCluster[clusterIndex]) continue;

                var p = doc.Pixels[i];
                double newHue = targetHues[clusterIndex];

                var newColor = MainWindow.HSLtoRGB(newHue, p.S, p.L);
                var old = doc.Working.GetPixel(p.X, p.Y);

                doc.Working.SetPixel(p.X, p.Y,
                    DrawingColor.FromArgb(old.A, newColor.R, newColor.G, newColor.B));
            }
        }
    }
}
