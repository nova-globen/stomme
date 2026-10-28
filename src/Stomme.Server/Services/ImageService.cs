using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Shared;
using SkiaSharp;
using System;
using System.Globalization;
using System.IO;

namespace Stomme.Services
{
    public class ImageService : IImageService
    {
        private readonly ILogManager _logger;

        private static readonly string[] _formats =
        {
            "png",
            "webp"
        };

        // SkiaSharp 3.x replacement for SKFilterQuality.
        private static readonly SKSamplingOptions TransformSampling =
            new(SKFilterMode.Linear);

        private static readonly SKSamplingOptions ResizeSampling =
            new(SKFilterMode.Linear, SKMipmapMode.Linear);

        public ImageService(ILogManager logger)
        {
            _logger = logger;
        }

        public string[] GetAvailableFormats()
        {
            return _formats;
        }

        public string CreateImage(
            string filepath,
            int width,
            int height,
            string mode,
            string position,
            string background,
            string rotate,
            string format,
            string imagepath)
        {
            try
            {
                SkiaResizeMode resizeMode = ParseResizeMode(mode);
                SkiaAnchorPosition anchorPosition = ParseAnchorPosition(position);

                SKColor backgroundColor = ParseBackgroundColor(
                    background,
                    out bool transparent);

                int angle = ParseRotation(rotate);
                format = ParseFormat(format);

                using FileStream inputStream = new(
                    filepath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);

                using SKCodec codec = SKCodec.Create(
                    inputStream,
                    out SKCodecResult codecResult);

                if (codec is null)
                {
                    throw new InvalidDataException(
                        $"The image could not be decoded. Codec result: {codecResult}.");
                }

                // This matches the original ImageSharp implementation, which
                // obtained the dimensions before AutoOrient and Rotate.
                int originalWidth = codec.Info.Width;
                int originalHeight = codec.Info.Height;

                using SKBitmap decoded = SKBitmap.Decode(codec);

                if (decoded is null)
                {
                    throw new InvalidDataException(
                        $"The image pixels could not be decoded. Codec result: {codecResult}.");
                }

                using SKBitmap oriented = AutoOrient(
                    decoded,
                    codec.EncodedOrigin);

                using SKBitmap rotated = Rotate(
                    oriented,
                    angle);

                if (width == 0 && height == 0)
                {
                    width = originalWidth;
                    height = originalHeight;
                }

                ValidateRequestedDimensions(width, height);

                using SKBitmap resized = Resize(
                    rotated,
                    width,
                    height,
                    resizeMode,
                    anchorPosition,
                    transparent
                        ? SKColors.Transparent
                        : backgroundColor);

                SaveImage(
                    resized,
                    imagepath,
                    format);
            }
            catch (Exception ex)
            {
                _logger.Log(
                    LogLevel.Error,
                    this,
                    LogFunction.Security,
                    ex,
                    "Error Creating Image For File {FilePath} {Width} {Height} {Mode} {Rotate} {Error}",
                    filepath,
                    width,
                    height,
                    mode,
                    rotate,
                    ex.Message);

                imagepath = string.Empty;
            }

            return imagepath;
        }

        private static SKBitmap AutoOrient(
            SKBitmap source,
            SKEncodedOrigin origin)
        {
            bool swapsDimensions =
                origin == SKEncodedOrigin.LeftTop ||
                origin == SKEncodedOrigin.RightTop ||
                origin == SKEncodedOrigin.RightBottom ||
                origin == SKEncodedOrigin.LeftBottom;

            int outputWidth = swapsDimensions
                ? source.Height
                : source.Width;

            int outputHeight = swapsDimensions
                ? source.Width
                : source.Height;

            SKBitmap result = CreateBitmap(
                outputWidth,
                outputHeight);

            SKMatrix matrix = CreateOrientationMatrix(
                origin,
                source.Width,
                source.Height);

            using SKCanvas canvas = new(result);

            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(matrix);

            canvas.DrawBitmap(
                source,
                new SKRect(
                    0,
                    0,
                    source.Width,
                    source.Height),
                TransformSampling);

            canvas.Flush();

            return result;
        }

        private static SKMatrix CreateOrientationMatrix(
            SKEncodedOrigin origin,
            int width,
            int height)
        {
            return origin switch
            {
                // x' = x, y' = y
                SKEncodedOrigin.TopLeft => CreateMatrix(
                    1, 0, 0,
                    0, 1, 0),

                // Horizontal reflection.
                // x' = width - x, y' = y
                SKEncodedOrigin.TopRight => CreateMatrix(
                    -1, 0, width,
                    0, 1, 0),

                // 180-degree rotation.
                // x' = width - x, y' = height - y
                SKEncodedOrigin.BottomRight => CreateMatrix(
                    -1, 0, width,
                    0, -1, height),

                // Vertical reflection.
                // x' = x, y' = height - y
                SKEncodedOrigin.BottomLeft => CreateMatrix(
                    1, 0, 0,
                    0, -1, height),

                // Reflection across the top-left to bottom-right diagonal.
                // x' = y, y' = x
                SKEncodedOrigin.LeftTop => CreateMatrix(
                    0, 1, 0,
                    1, 0, 0),

                // 90-degree clockwise rotation.
                // x' = height - y, y' = x
                SKEncodedOrigin.RightTop => CreateMatrix(
                    0, -1, height,
                    1, 0, 0),

                // Reflection across the opposite diagonal.
                // x' = height - y, y' = width - x
                SKEncodedOrigin.RightBottom => CreateMatrix(
                    0, -1, height,
                    -1, 0, width),

                // 90-degree counter-clockwise rotation.
                // x' = y, y' = width - x
                SKEncodedOrigin.LeftBottom => CreateMatrix(
                    0, 1, 0,
                    -1, 0, width),

                _ => SKMatrix.Identity
            };
        }

        private static SKBitmap Rotate(
            SKBitmap source,
            int angle)
        {
            angle %= 360;

            if (angle == 0)
            {
                return CopyBitmap(source);
            }

            GetSinCos(
                angle,
                out double sine,
                out double cosine);

            double width = source.Width;
            double height = source.Height;

            double x1 = 0;
            double y1 = 0;

            double x2 = width * cosine;
            double y2 = width * sine;

            double x3 = -height * sine;
            double y3 = height * cosine;

            double x4 = (width * cosine) - (height * sine);
            double y4 = (width * sine) + (height * cosine);

            double minimumX = Math.Min(
                Math.Min(x1, x2),
                Math.Min(x3, x4));

            double maximumX = Math.Max(
                Math.Max(x1, x2),
                Math.Max(x3, x4));

            double minimumY = Math.Min(
                Math.Min(y1, y2),
                Math.Min(y3, y4));

            double maximumY = Math.Max(
                Math.Max(y1, y2),
                Math.Max(y3, y4));

            int outputWidth = Math.Max(
                1,
                (int)Math.Ceiling(maximumX - minimumX));

            int outputHeight = Math.Max(
                1,
                (int)Math.Ceiling(maximumY - minimumY));

            SKBitmap result = CreateBitmap(
                outputWidth,
                outputHeight);

            SKMatrix rotationMatrix = CreateMatrix(
                (float)cosine,
                (float)-sine,
                (float)-minimumX,
                (float)sine,
                (float)cosine,
                (float)-minimumY);

            using SKCanvas canvas = new(result);

            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(rotationMatrix);

            canvas.DrawBitmap(
                source,
                new SKRect(
                    0,
                    0,
                    source.Width,
                    source.Height),
                TransformSampling);

            canvas.Flush();

            return result;
        }

        private static SKBitmap Resize(
            SKBitmap source,
            int requestedWidth,
            int requestedHeight,
            SkiaResizeMode mode,
            SkiaAnchorPosition anchor,
            SKColor backgroundColor)
        {
            ResolveMissingDimension(
                source.Width,
                source.Height,
                ref requestedWidth,
                ref requestedHeight);

            return mode switch
            {
                SkiaResizeMode.Crop => ResizeCrop(
                    source,
                    requestedWidth,
                    requestedHeight,
                    anchor,
                    backgroundColor),

                SkiaResizeMode.Pad => ResizePad(
                    source,
                    requestedWidth,
                    requestedHeight,
                    anchor,
                    backgroundColor,
                    preventUpscaling: false),

                SkiaResizeMode.BoxPad => ResizePad(
                    source,
                    requestedWidth,
                    requestedHeight,
                    anchor,
                    backgroundColor,
                    preventUpscaling: true),

                SkiaResizeMode.Max => ResizeMax(
                    source,
                    requestedWidth,
                    requestedHeight,
                    backgroundColor),

                SkiaResizeMode.Min => ResizeMin(
                    source,
                    requestedWidth,
                    requestedHeight,
                    backgroundColor),

                SkiaResizeMode.Stretch => ResizeStretch(
                    source,
                    requestedWidth,
                    requestedHeight,
                    backgroundColor),

                _ => ResizeCrop(
                    source,
                    requestedWidth,
                    requestedHeight,
                    anchor,
                    backgroundColor)
            };
        }

        private static SKBitmap ResizeCrop(
            SKBitmap source,
            int targetWidth,
            int targetHeight,
            SkiaAnchorPosition anchor,
            SKColor backgroundColor)
        {
            float sourceAspectRatio =
                source.Width / (float)source.Height;

            float targetAspectRatio =
                targetWidth / (float)targetHeight;

            float cropWidth;
            float cropHeight;

            if (sourceAspectRatio > targetAspectRatio)
            {
                cropHeight = source.Height;
                cropWidth = cropHeight * targetAspectRatio;
            }
            else
            {
                cropWidth = source.Width;
                cropHeight = cropWidth / targetAspectRatio;
            }

            float cropX = GetHorizontalOffset(
                source.Width - cropWidth,
                anchor);

            float cropY = GetVerticalOffset(
                source.Height - cropHeight,
                anchor);

            SKRect sourceRectangle = new(
                cropX,
                cropY,
                cropX + cropWidth,
                cropY + cropHeight);

            SKRect destinationRectangle = new(
                0,
                0,
                targetWidth,
                targetHeight);

            return DrawResizedBitmap(
                source,
                targetWidth,
                targetHeight,
                sourceRectangle,
                destinationRectangle,
                backgroundColor);
        }

        private static SKBitmap ResizePad(
            SKBitmap source,
            int targetWidth,
            int targetHeight,
            SkiaAnchorPosition anchor,
            SKColor backgroundColor,
            bool preventUpscaling)
        {
            double scale = Math.Min(
                targetWidth / (double)source.Width,
                targetHeight / (double)source.Height);

            if (preventUpscaling)
            {
                scale = Math.Min(1D, scale);
            }

            float destinationWidth =
                (float)(source.Width * scale);

            float destinationHeight =
                (float)(source.Height * scale);

            float destinationX = GetHorizontalOffset(
                targetWidth - destinationWidth,
                anchor);

            float destinationY = GetVerticalOffset(
                targetHeight - destinationHeight,
                anchor);

            SKRect destinationRectangle = new(
                destinationX,
                destinationY,
                destinationX + destinationWidth,
                destinationY + destinationHeight);

            return DrawResizedBitmap(
                source,
                targetWidth,
                targetHeight,
                new SKRect(
                    0,
                    0,
                    source.Width,
                    source.Height),
                destinationRectangle,
                backgroundColor);
        }

        private static SKBitmap ResizeMax(
            SKBitmap source,
            int targetWidth,
            int targetHeight,
            SKColor backgroundColor)
        {
            double scale = Math.Min(
                targetWidth / (double)source.Width,
                targetHeight / (double)source.Height);

            int outputWidth = RoundDimension(
                source.Width * scale);

            int outputHeight = RoundDimension(
                source.Height * scale);

            return DrawResizedBitmap(
                source,
                outputWidth,
                outputHeight,
                new SKRect(
                    0,
                    0,
                    source.Width,
                    source.Height),
                new SKRect(
                    0,
                    0,
                    outputWidth,
                    outputHeight),
                backgroundColor);
        }

        private static SKBitmap ResizeMin(
            SKBitmap source,
            int targetWidth,
            int targetHeight,
            SKColor backgroundColor)
        {
            double scale = Math.Max(
                targetWidth / (double)source.Width,
                targetHeight / (double)source.Height);

            // ImageSharp Min does not upscale.
            scale = Math.Min(1D, scale);

            int outputWidth = RoundDimension(
                source.Width * scale);

            int outputHeight = RoundDimension(
                source.Height * scale);

            return DrawResizedBitmap(
                source,
                outputWidth,
                outputHeight,
                new SKRect(
                    0,
                    0,
                    source.Width,
                    source.Height),
                new SKRect(
                    0,
                    0,
                    outputWidth,
                    outputHeight),
                backgroundColor);
        }

        private static SKBitmap ResizeStretch(
            SKBitmap source,
            int targetWidth,
            int targetHeight,
            SKColor backgroundColor)
        {
            return DrawResizedBitmap(
                source,
                targetWidth,
                targetHeight,
                new SKRect(
                    0,
                    0,
                    source.Width,
                    source.Height),
                new SKRect(
                    0,
                    0,
                    targetWidth,
                    targetHeight),
                backgroundColor);
        }

        private static SKBitmap DrawResizedBitmap(
            SKBitmap source,
            int outputWidth,
            int outputHeight,
            SKRect sourceRectangle,
            SKRect destinationRectangle,
            SKColor backgroundColor)
        {
            SKBitmap result = CreateBitmap(
                outputWidth,
                outputHeight);

            using SKCanvas canvas = new(result);
            using SKPaint paint = new()
            {
                IsAntialias = true
            };

            canvas.Clear(backgroundColor);

            canvas.DrawBitmap(
                source,
                sourceRectangle,
                destinationRectangle,
                ResizeSampling,
                paint);

            canvas.Flush();

            return result;
        }

        private static SKBitmap CopyBitmap(SKBitmap source)
        {
            SKBitmap result = CreateBitmap(
                source.Width,
                source.Height);

            using SKCanvas canvas = new(result);

            canvas.Clear(SKColors.Transparent);

            canvas.DrawBitmap(
                source,
                new SKRect(
                    0,
                    0,
                    source.Width,
                    source.Height),
                TransformSampling);

            canvas.Flush();

            return result;
        }

        private static SKBitmap CreateBitmap(
            int width,
            int height)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(width),
                    "The image width must be greater than zero.");
            }

            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(height),
                    "The image height must be greater than zero.");
            }

            return new SKBitmap(
                new SKImageInfo(
                    width,
                    height,
                    SKColorType.Rgba8888,
                    SKAlphaType.Premul));
        }

        private static void SaveImage(
            SKBitmap bitmap,
            string imagePath,
            string format)
        {
            SKEncodedImageFormat encodedFormat =
                format == "webp"
                    ? SKEncodedImageFormat.Webp
                    : SKEncodedImageFormat.Png;

            int quality =
                encodedFormat == SKEncodedImageFormat.Webp
                    ? 60
                    : 100;

            using FileStream outputStream = new(
                imagePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);

            bool encoded = bitmap.Encode(
                outputStream,
                encodedFormat,
                quality);

            if (!encoded)
            {
                throw new InvalidOperationException(
                    $"SkiaSharp could not encode the image as {format}.");
            }
        }

        private static void ResolveMissingDimension(
            int sourceWidth,
            int sourceHeight,
            ref int width,
            ref int height)
        {
            if (width == 0 && height > 0)
            {
                width = RoundDimension(
                    sourceWidth *
                    (height / (double)sourceHeight));
            }
            else if (height == 0 && width > 0)
            {
                height = RoundDimension(
                    sourceHeight *
                    (width / (double)sourceWidth));
            }

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(width),
                    "The resulting image dimensions must be greater than zero.");
            }
        }

        private static void ValidateRequestedDimensions(
            int width,
            int height)
        {
            if (width < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(width),
                    "The requested width cannot be negative.");
            }

            if (height < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(height),
                    "The requested height cannot be negative.");
            }
        }

        private static int RoundDimension(double value)
        {
            return Math.Max(
                1,
                (int)Math.Round(
                    value,
                    MidpointRounding.AwayFromZero));
        }

        private static float GetHorizontalOffset(
            float availableSpace,
            SkiaAnchorPosition anchor)
        {
            return anchor switch
            {
                SkiaAnchorPosition.Left or
                SkiaAnchorPosition.TopLeft or
                SkiaAnchorPosition.BottomLeft => 0,

                SkiaAnchorPosition.Right or
                SkiaAnchorPosition.TopRight or
                SkiaAnchorPosition.BottomRight => availableSpace,

                _ => availableSpace / 2F
            };
        }

        private static float GetVerticalOffset(
            float availableSpace,
            SkiaAnchorPosition anchor)
        {
            return anchor switch
            {
                SkiaAnchorPosition.Top or
                SkiaAnchorPosition.TopLeft or
                SkiaAnchorPosition.TopRight => 0,

                SkiaAnchorPosition.Bottom or
                SkiaAnchorPosition.BottomLeft or
                SkiaAnchorPosition.BottomRight => availableSpace,

                _ => availableSpace / 2F
            };
        }

        private static SKMatrix CreateMatrix(
            float scaleX,
            float skewX,
            float translateX,
            float skewY,
            float scaleY,
            float translateY)
        {
            return new SKMatrix
            {
                ScaleX = scaleX,
                SkewX = skewX,
                TransX = translateX,

                SkewY = skewY,
                ScaleY = scaleY,
                TransY = translateY,

                Persp0 = 0,
                Persp1 = 0,
                Persp2 = 1
            };
        }

        private static void GetSinCos(
            int angle,
            out double sine,
            out double cosine)
        {
            // Avoid floating-point dimension errors for right-angle rotations.
            switch (angle)
            {
                case 90:
                    sine = 1;
                    cosine = 0;
                    return;

                case 180:
                    sine = 0;
                    cosine = -1;
                    return;

                case 270:
                    sine = -1;
                    cosine = 0;
                    return;
            }

            double radians =
                angle * Math.PI / 180D;

            sine = Math.Sin(radians);
            cosine = Math.Cos(radians);
        }

        private static int ParseRotation(string rotate)
        {
            if (!int.TryParse(
                    rotate,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int angle) ||
                angle < 0 ||
                angle > 360)
            {
                return 0;
            }

            return angle == 360
                ? 0
                : angle;
        }

        private static SKColor ParseBackgroundColor(
            string background,
            out bool transparent)
        {
            transparent = string.Equals(
                background,
                "transparent",
                StringComparison.OrdinalIgnoreCase);

            if (transparent)
            {
                return SKColors.Transparent;
            }

            string colorText =
                background?.Trim() ?? string.Empty;

            if (!colorText.StartsWith(
                    "#",
                    StringComparison.Ordinal))
            {
                colorText = "#" + colorText;
            }

            if (!SKColor.TryParse(
                    colorText,
                    out SKColor parsedColor))
            {
                transparent = true;
                return SKColors.Transparent;
            }

            // The old non-transparent encoder removed the alpha channel.
            return new SKColor(
                parsedColor.Red,
                parsedColor.Green,
                parsedColor.Blue,
                255);
        }

        private static string ParseFormat(string format)
        {
            string normalized =
                format?.Trim().ToLowerInvariant() ?? string.Empty;

            foreach (string availableFormat in _formats)
            {
                if (string.Equals(
                        normalized,
                        availableFormat,
                        StringComparison.Ordinal))
                {
                    return normalized;
                }
            }

            return "png";
        }

        private static SkiaResizeMode ParseResizeMode(string mode)
        {
            if (!Enum.TryParse(
                    mode,
                    ignoreCase: true,
                    out SkiaResizeMode resizeMode))
            {
                return SkiaResizeMode.Crop;
            }

            // The service has no TargetRectangle parameter, so ImageSharp's
            // Manual mode cannot be represented by this method.
            return resizeMode == SkiaResizeMode.Manual
                ? SkiaResizeMode.Crop
                : resizeMode;
        }

        private static SkiaAnchorPosition ParseAnchorPosition(
            string position)
        {
            return Enum.TryParse(
                position,
                ignoreCase: true,
                out SkiaAnchorPosition anchor)
                    ? anchor
                    : SkiaAnchorPosition.Center;
        }

        private enum SkiaResizeMode
        {
            Crop,
            Pad,
            BoxPad,
            Max,
            Min,
            Stretch,
            Manual
        }

        private enum SkiaAnchorPosition
        {
            Center,
            Top,
            Bottom,
            Left,
            Right,
            TopLeft,
            TopRight,
            BottomRight,
            BottomLeft
        }
    }
}