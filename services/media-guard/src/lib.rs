use image::{DynamicImage, ImageFormat, ImageReader, Limits};
use std::io::Cursor;

pub const MAX_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_SIDE: u32 = 4096;

pub struct SanitizedImage {
    pub image: Vec<u8>,
    pub thumbnail: Vec<u8>,
}

/// Decode only allowlisted formats and encode fresh pixels. Metadata and trailing
/// payloads are never copied. All consumers receive PNG regardless of input type.
pub fn sanitize(bytes: &[u8]) -> Result<SanitizedImage, &'static str> {
    if bytes.is_empty() || bytes.len() > MAX_BYTES {
        return Err("image must contain between 1 byte and 8 MiB");
    }
    let format = image::guess_format(bytes).map_err(|_| "unsupported image content")?;
    if !matches!(
        format,
        ImageFormat::Png | ImageFormat::Bmp | ImageFormat::Jpeg
    ) {
        return Err("unsupported image content");
    }
    let mut reader = ImageReader::with_format(Cursor::new(bytes), format);
    let mut limits = Limits::default();
    limits.max_image_width = Some(MAX_SIDE);
    limits.max_image_height = Some(MAX_SIDE);
    limits.max_alloc = Some(128 * 1024 * 1024);
    reader.limits(limits);
    let decoded = reader
        .decode()
        .map_err(|_| "invalid image or dimensions exceed 4096 pixels")?;
    // A fresh pixel buffer also discards EXIF, ICC, comments and orientation tags.
    let pixels = DynamicImage::ImageRgb8(decoded.to_rgb8());
    Ok(SanitizedImage {
        image: encode(&pixels)?,
        thumbnail: encode(&pixels.thumbnail(256, 256))?,
    })
}

fn encode(image: &DynamicImage) -> Result<Vec<u8>, &'static str> {
    let mut output = Cursor::new(Vec::new());
    image
        .write_to(&mut output, ImageFormat::Png)
        .map_err(|_| "image encoding failed")?;
    Ok(output.into_inner())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_html_even_when_caller_names_it_png() {
        assert!(sanitize(b"<html><script>alert(1)</script></html>").is_err());
    }

    #[test]
    fn strips_appended_html_and_generates_bounded_thumbnail() {
        let input = DynamicImage::new_rgb8(600, 300);
        let mut bytes = encode(&input).unwrap();
        bytes.extend_from_slice(b"<script>evil()</script>");
        let result = sanitize(&bytes).unwrap();
        assert!(!result.image.windows(8).any(|w| w == b"<script>"));
        assert_eq!(image::load_from_memory(&result.image).unwrap().width(), 600);
        let thumb = image::load_from_memory(&result.thumbnail).unwrap();
        assert_eq!((thumb.width(), thumb.height()), (256, 128));
    }

    #[test]
    fn rejects_empty_oversize_and_excess_dimensions() {
        assert!(sanitize(&[]).is_err());
        assert!(sanitize(&vec![0; MAX_BYTES + 1]).is_err());
        assert!(sanitize(&encode(&DynamicImage::new_rgb8(4097, 1)).unwrap()).is_err());
        assert!(sanitize(b"\x89PNG\r\n\x1a\ntruncated").is_err());
    }
}
