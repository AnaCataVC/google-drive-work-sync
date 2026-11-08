use std::ffi::{CStr, CString};
use std::fs::File;
use std::io::{self, Read};
use std::os::raw::c_char;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU8, Ordering};
use std::sync::Arc;
use rayon::prelude::*;
use serde::{Deserialize, Serialize};

#[cfg(windows)]
use std::os::windows::fs::{MetadataExt, OpenOptionsExt};

// .NET DateTime.UtcNow.Ticks epoch offset: difference between 0001-01-01 and 1601-01-01 (FILETIME) in 100ns ticks
const TICKS_BETWEEN_1_AND_1601: u64 = 504_911_232_000_000_000;

// Wrapper struct around raw pointer to AtomicU8 so Rayon can share it across threads safely
#[derive(Copy, Clone)]
pub struct CancelToken(pub *const AtomicU8);
unsafe impl Send for CancelToken {}
unsafe impl Sync for CancelToken {}

impl CancelToken {
    #[inline]
    fn is_cancelled(&self) -> bool {
        if self.0.is_null() {
            false
        } else {
            unsafe { (*self.0).load(Ordering::Relaxed) != 0 }
        }
    }
}

/// Open file with full sharing (equivalent to FileShare.ReadWrite in C#)
#[inline]
fn open_file_shared(path: &Path) -> io::Result<File> {
    #[cfg(windows)]
    {
        // FILE_SHARE_READ (1) | FILE_SHARE_WRITE (2) | FILE_SHARE_DELETE (4) = 7
        std::fs::OpenOptions::new()
            .read(true)
            .share_mode(7)
            .open(path)
    }
    #[cfg(not(windows))]
    {
        File::open(path)
    }
}

/// Capture (file_size, last_write_time_ticks) before reading contents (Invariant #1)
#[inline]
fn get_file_metadata(path: &Path) -> io::Result<(u64, u64)> {
    let meta = std::fs::metadata(path)?;
    #[cfg(windows)]
    {
        let filetime = meta.last_write_time();
        let ticks = filetime.saturating_add(TICKS_BETWEEN_1_AND_1601);
        Ok((meta.file_size(), ticks))
    }
    #[cfg(not(windows))]
    {
        let len = meta.len();
        let mtime = meta.modified().unwrap_or(std::time::UNIX_EPOCH);
        let duration = mtime.duration_since(std::time::UNIX_EPOCH).unwrap_or_default();
        // 1970 to 0001 ticks offset = 621355968000000000
        let ticks = 621_355_968_000_000_000 + (duration.as_nanos() / 100) as u64;
        Ok((len, ticks))
    }
}

#[inline]
fn is_reparse_point(path: &Path) -> bool {
    #[cfg(windows)]
    {
        if let Ok(meta) = std::fs::symlink_metadata(path) {
            // FILE_ATTRIBUTE_REPARSE_POINT = 0x0400
            (meta.file_attributes() & 0x0400) != 0
        } else {
            false
        }
    }
    #[cfg(not(windows))]
    {
        path.is_symlink()
    }
}

/// Compute BLAKE3 hash of a file stream in 64 KB chunks, updating hasher.
pub fn hash_file_blake3(path: &Path, cancel_token: CancelToken) -> io::Result<[u8; 32]> {
    let mut file = open_file_shared(path)?;
    let mut hasher = blake3::Hasher::new();
    let mut buffer = [0u8; 65536];

    loop {
        if cancel_token.is_cancelled() {
            return Err(io::Error::new(io::ErrorKind::Interrupted, "Operation cancelled"));
        }
        let n = file.read(&mut buffer)?;
        if n == 0 {
            break;
        }
        hasher.update(&buffer[..n]);
    }

    Ok(*hasher.finalize().as_bytes())
}

/// Compute BLAKE3 hash of up to `max_bytes` of a file (header hash for duplicate detection).
pub fn hash_file_header_blake3(
    path: &Path,
    max_bytes: usize,
    cancel_token: CancelToken,
) -> io::Result<([u8; 32], usize)> {
    let mut file = open_file_shared(path)?;
    let mut buffer = vec![0u8; max_bytes.min(65536)];
    let mut total_read = 0;
    let mut hasher = blake3::Hasher::new();

    while total_read < max_bytes {
        if cancel_token.is_cancelled() {
            return Err(io::Error::new(io::ErrorKind::Interrupted, "Operation cancelled"));
        }
        let to_read = (max_bytes - total_read).min(buffer.len());
        let n = file.read(&mut buffer[..to_read])?;
        if n == 0 {
            break;
        }
        hasher.update(&buffer[..n]);
        total_read += n;
    }

    Ok((*hasher.finalize().as_bytes(), total_read))
}

// ---------------------------------------------------------------------------
// DTOs for JSON FFI
// ---------------------------------------------------------------------------

#[derive(Deserialize)]
pub struct FilterOptionsDto {
    pub included_extensions: Vec<String>,
    pub excluded_extensions: Vec<String>,
    pub excluded_folders: Vec<String>,
    pub max_file_size_bytes: u64,
}

#[derive(Deserialize)]
pub struct SyncSourceDto {
    pub local_folder_path: String,
    pub destination_prefix: String,
}

#[derive(Deserialize)]
pub struct KnownHashEntryDto {
    pub hash: String,
    pub last_write_time_utc_ticks: u64,
    pub file_size: u64,
}

#[derive(Deserialize)]
pub struct ScanAndDiffRequestDto {
    pub sources: Vec<SyncSourceDto>,
    pub filters: FilterOptionsDto,
    pub only_modified_or_new: bool,
    pub force_full_sync: bool,
    pub known_hashes: std::collections::HashMap<String, KnownHashEntryDto>,
}

#[derive(Serialize)]
pub struct ScannedFileDto {
    pub file_path: String,
    pub file_name: String,
    pub relative_path: String,
    pub hash_key: String,
    pub file_size: u64,
    pub hash: String,
    pub classification: String, // "Unchanged", "MetadataStale", "NeedsUpload", "Unreadable"
    pub is_new: bool,
    pub error_code: i32,
    pub error_message: String,
}

#[derive(Serialize)]
pub struct ScanAndDiffResponseDto {
    pub success: bool,
    pub cancelled: bool,
    pub scanned_files: Vec<ScannedFileDto>,
    pub unreadable_directories: Vec<String>,
}

#[derive(Deserialize)]
pub struct BatchHashRequestDto {
    pub file_paths: Vec<String>,
}

#[derive(Serialize)]
pub struct BatchHashResultItemDto {
    pub file_path: String,
    pub hash: String,
    pub file_size: u64,
    pub last_write_time_utc_ticks: u64,
    pub success: bool,
    pub error_code: i32,
}

#[derive(Serialize)]
pub struct BatchHashResponseDto {
    pub results: Vec<BatchHashResultItemDto>,
    pub cancelled: bool,
}

// ---------------------------------------------------------------------------
// Engine Logic
// ---------------------------------------------------------------------------

impl FilterOptionsDto {
    fn is_folder_excluded(&self, folder_name: &str) -> bool {
        self.excluded_folders
            .iter()
            .any(|f| f.eq_ignore_ascii_case(folder_name))
    }

    fn should_include_file(&self, file_name: &str, file_size: u64) -> bool {
        let ext = Path::new(file_name)
            .extension()
            .and_then(|s| s.to_str())
            .map(|s| format!(".{}", s.to_ascii_lowercase()))
            .unwrap_or_default();

        if self.excluded_extensions.iter().any(|e| {
            let mut normalized = e.to_ascii_lowercase();
            if !normalized.starts_with('.') {
                normalized = format!(".{}", normalized);
            }
            normalized == ext
        }) {
            return false;
        }

        if !self.included_extensions.is_empty() {
            let in_whitelist = self.included_extensions.iter().any(|e| {
                let mut normalized = e.to_ascii_lowercase();
                if !normalized.starts_with('.') {
                    normalized = format!(".{}", normalized);
                }
                normalized == ext
            });
            if !in_whitelist {
                return false;
            }
        }

        if self.max_file_size_bytes > 0 && file_size > self.max_file_size_bytes {
            return false;
        }

        true
    }
}

fn scan_directory_tree(
    root: &Path,
    filters: &FilterOptionsDto,
    unreadable_dirs: &mut Vec<String>,
    cancel_token: CancelToken,
) -> Vec<(PathBuf, String, u64, u64)> {
    let mut files = Vec::new();
    let mut queue = Vec::new();

    if !root.exists() {
        return files;
    }

    queue.push(root.to_path_buf());

    while let Some(current_dir) = queue.pop() {
        if cancel_token.is_cancelled() {
            break;
        }

        let read_dir = match std::fs::read_dir(&current_dir) {
            Ok(rd) => rd,
            Err(_) => {
                unreadable_dirs.push(current_dir.to_string_lossy().to_string());
                continue;
            }
        };

        for entry_res in read_dir {
            let entry = match entry_res {
                Ok(e) => e,
                Err(_) => {
                    unreadable_dirs.push(current_dir.to_string_lossy().to_string());
                    continue;
                }
            };

            let path = entry.path();
            let file_name = entry.file_name().to_string_lossy().to_string();

            let file_type = match entry.file_type() {
                Ok(ft) => ft,
                Err(_) => continue,
            };

            if file_type.is_dir() {
                if !filters.is_folder_excluded(&file_name) && !is_reparse_point(&path) {
                    queue.push(path);
                }
            } else if file_type.is_file() {
                match get_file_metadata(&path) {
                    Ok((len, ticks)) => {
                        if filters.should_include_file(&file_name, len) {
                            files.push((path, file_name, len, ticks));
                        }
                    }
                    Err(_) => {
                        // Unreadable file will be processed during classification
                        files.push((path, file_name, 0, 0));
                    }
                }
            }
        }
    }

    files
}

pub fn scan_and_diff(
    request: ScanAndDiffRequestDto,
    cancel_flag: *const AtomicU8,
) -> ScanAndDiffResponseDto {
    let cancel_token = CancelToken(cancel_flag);
    let mut all_scanned = Vec::new();
    let mut unreadable_directories = Vec::new();

    for source in &request.sources {
        if cancel_token.is_cancelled() {
            return ScanAndDiffResponseDto {
                success: false,
                cancelled: true,
                scanned_files: Vec::new(),
                unreadable_directories,
            };
        }

        let root = Path::new(&source.local_folder_path);
        let discovered = scan_directory_tree(
            root,
            &request.filters,
            &mut unreadable_directories,
            cancel_token,
        );

        let prefix = &source.destination_prefix;
        for (abs_path, file_name, file_size, ticks) in discovered {
            let rel_from_root = pathdiff::diff_paths(&abs_path, root)
                .unwrap_or_else(|| PathBuf::from(&file_name))
                .to_string_lossy()
                .replace('\\', "/");

            let combined_rel = if prefix.is_empty() {
                rel_from_root.trim_start_matches('/').to_string()
            } else {
                format!("{}/{}", prefix.trim_matches('/'), rel_from_root.trim_start_matches('/'))
            };

            let abs_path_str = abs_path.to_string_lossy().to_string();
            let hash_key = format!("{}|{}", prefix, abs_path_str);

            all_scanned.push((abs_path_str, file_name, combined_rel, hash_key, file_size, ticks));
        }
    }

    let known_hashes = Arc::new(request.known_hashes);
    let only_modified = request.only_modified_or_new;
    let force_full = request.force_full_sync;

    // Parallel classification using Rayon!
    let classified_files: Vec<ScannedFileDto> = all_scanned
        .into_par_iter()
        .map(|(file_path, file_name, relative_path, hash_key, file_size, ticks)| {
            if cancel_token.is_cancelled() {
                return ScannedFileDto {
                    file_path,
                    file_name,
                    relative_path,
                    hash_key,
                    file_size,
                    hash: String::new(),
                    classification: "Unchanged".to_string(),
                    is_new: false,
                    error_code: 0,
                    error_message: String::new(),
                };
            }

            let path_obj = Path::new(&file_path);
            let known = known_hashes.get(&hash_key);
            let is_new = known.is_none();

            // Fast path metadata check
            if !force_full && only_modified {
                if let Some(entry) = known {
                    if entry.last_write_time_utc_ticks > 0
                        && entry.last_write_time_utc_ticks == ticks
                        && entry.file_size == file_size
                    {
                        return ScannedFileDto {
                            file_path,
                            file_name,
                            relative_path,
                            hash_key,
                            file_size,
                            hash: entry.hash.clone(),
                            classification: "Unchanged".to_string(),
                            is_new: false,
                            error_code: 0,
                            error_message: String::new(),
                        };
                    }
                }
            }

            // Needs hash calculation
            match hash_file_blake3(path_obj, cancel_token) {
                Ok(bytes) => {
                    let hash_hex = hex::encode(bytes);
                    let classification = if let Some(entry) = known {
                        if entry.hash.eq_ignore_ascii_case(&hash_hex) {
                            "MetadataStale".to_string()
                        } else {
                            "NeedsUpload".to_string()
                        }
                    } else {
                        "NeedsUpload".to_string()
                    };

                    ScannedFileDto {
                        file_path,
                        file_name,
                        relative_path,
                        hash_key,
                        file_size,
                        hash: hash_hex,
                        classification,
                        is_new,
                        error_code: 0,
                        error_message: String::new(),
                    }
                }
                Err(err) => {
                    let code = err.raw_os_error().unwrap_or(-1);
                    ScannedFileDto {
                        file_path,
                        file_name,
                        relative_path,
                        hash_key,
                        file_size,
                        hash: String::new(),
                        classification: "Unreadable".to_string(),
                        is_new: false,
                        error_code: code,
                        error_message: err.to_string(),
                    }
                }
            }
        })
        .collect();

    let was_cancelled = cancel_token.is_cancelled();

    ScanAndDiffResponseDto {
        success: !was_cancelled,
        cancelled: was_cancelled,
        scanned_files: classified_files,
        unreadable_directories,
    }
}

// ---------------------------------------------------------------------------
// C-ABI Exports (Protected with std::panic::catch_unwind)
// ---------------------------------------------------------------------------

/// Computes BLAKE3 hash of a file into a caller-allocated 32-byte buffer.
/// Returns 0 on success, or OS error code on failure.
#[no_mangle]
pub unsafe extern "C" fn rust_compute_blake3_file(
    path_ptr: *const c_char,
    out_hash_32: *mut u8,
    cancel_flag: *const AtomicU8,
) -> i32 {
    let result = std::panic::catch_unwind(|| {
        if path_ptr.is_null() || out_hash_32.is_null() {
            return -1;
        }

        let c_str = CStr::from_ptr(path_ptr);
        let path_str = match c_str.to_str() {
            Ok(s) => s,
            Err(_) => return -2,
        };

        match hash_file_blake3(Path::new(path_str), CancelToken(cancel_flag)) {
            Ok(hash) => {
                std::ptr::copy_nonoverlapping(hash.as_ptr(), out_hash_32, 32);
                0
            }
            Err(e) => e.raw_os_error().unwrap_or(-3),
        }
    });

    result.unwrap_or(-99)
}

/// Computes BLAKE3 header hash up to max_bytes into caller-allocated 32-byte buffer.
/// Sets *out_bytes_read to the actual bytes read.
#[no_mangle]
pub unsafe extern "C" fn rust_compute_blake3_header(
    path_ptr: *const c_char,
    max_bytes: usize,
    out_hash_32: *mut u8,
    out_bytes_read: *mut usize,
    cancel_flag: *const AtomicU8,
) -> i32 {
    let result = std::panic::catch_unwind(|| {
        if path_ptr.is_null() || out_hash_32.is_null() || out_bytes_read.is_null() {
            return -1;
        }

        let c_str = CStr::from_ptr(path_ptr);
        let path_str = match c_str.to_str() {
            Ok(s) => s,
            Err(_) => return -2,
        };

        match hash_file_header_blake3(Path::new(path_str), max_bytes, CancelToken(cancel_flag)) {
            Ok((hash, bytes_read)) => {
                std::ptr::copy_nonoverlapping(hash.as_ptr(), out_hash_32, 32);
                *out_bytes_read = bytes_read;
                0
            }
            Err(e) => e.raw_os_error().unwrap_or(-3),
        }
    });

    result.unwrap_or(-99)
}

/// Batch hashing of arbitrary files with Rayon. Returns UTF-8 JSON pointer.
#[no_mangle]
pub unsafe extern "C" fn rust_batch_hash_files_json(
    request_json_ptr: *const c_char,
    cancel_flag: *const AtomicU8,
) -> *mut c_char {
    let result = std::panic::catch_unwind(|| {
        if request_json_ptr.is_null() {
            return std::ptr::null_mut();
        }

        let c_str = CStr::from_ptr(request_json_ptr);
        let json_str = match c_str.to_str() {
            Ok(s) => s,
            Err(_) => return std::ptr::null_mut(),
        };

        let request: BatchHashRequestDto = match serde_json::from_str(json_str) {
            Ok(r) => r,
            Err(_) => return std::ptr::null_mut(),
        };

        let cancel_token = CancelToken(cancel_flag);
        let results: Vec<BatchHashResultItemDto> = request
            .file_paths
            .into_par_iter()
            .map(|path_str| {
                if cancel_token.is_cancelled() {
                    return BatchHashResultItemDto {
                        file_path: path_str,
                        hash: String::new(),
                        file_size: 0,
                        last_write_time_utc_ticks: 0,
                        success: false,
                        error_code: 999,
                    };
                }

                let path_obj = Path::new(&path_str);
                let (len, ticks) = match get_file_metadata(path_obj) {
                    Ok(m) => m,
                    Err(e) => {
                        return BatchHashResultItemDto {
                            file_path: path_str,
                            hash: String::new(),
                            file_size: 0,
                            last_write_time_utc_ticks: 0,
                            success: false,
                            error_code: e.raw_os_error().unwrap_or(-1),
                        };
                    }
                };

                match hash_file_blake3(path_obj, cancel_token) {
                    Ok(h) => BatchHashResultItemDto {
                        file_path: path_str,
                        hash: hex::encode(h),
                        file_size: len,
                        last_write_time_utc_ticks: ticks,
                        success: true,
                        error_code: 0,
                    },
                    Err(e) => BatchHashResultItemDto {
                        file_path: path_str,
                        hash: String::new(),
                        file_size: len,
                        last_write_time_utc_ticks: ticks,
                        success: false,
                        error_code: e.raw_os_error().unwrap_or(-2),
                    },
                }
            })
            .collect();

        let response = BatchHashResponseDto {
            results,
            cancelled: cancel_token.is_cancelled(),
        };

        match serde_json::to_string(&response) {
            Ok(json_res) => CString::new(json_res).unwrap().into_raw(),
            Err(_) => std::ptr::null_mut(),
        }
    });

    result.unwrap_or(std::ptr::null_mut())
}

/// Executes parallel directory tree scan and state diffing against known hashes. Returns UTF-8 JSON pointer.
#[no_mangle]
pub unsafe extern "C" fn rust_scan_and_diff_json(
    request_json_ptr: *const c_char,
    cancel_flag: *const AtomicU8,
) -> *mut c_char {
    let result = std::panic::catch_unwind(|| {
        if request_json_ptr.is_null() {
            return std::ptr::null_mut();
        }

        let c_str = CStr::from_ptr(request_json_ptr);
        let json_str = match c_str.to_str() {
            Ok(s) => s,
            Err(_) => return std::ptr::null_mut(),
        };

        let request: ScanAndDiffRequestDto = match serde_json::from_str(json_str) {
            Ok(r) => r,
            Err(_) => return std::ptr::null_mut(),
        };

        let response = scan_and_diff(request, cancel_flag);

        match serde_json::to_string(&response) {
            Ok(json_res) => CString::new(json_res).unwrap().into_raw(),
            Err(_) => std::ptr::null_mut(),
        }
    });

    result.unwrap_or(std::ptr::null_mut())
}

/// Safely frees a string previously allocated by Rust (via CString::into_raw).
#[no_mangle]
pub unsafe extern "C" fn rust_free_string(ptr: *mut c_char) {
    if !ptr.is_null() {
        let _ = std::panic::catch_unwind(|| {
            let _ = CString::from_raw(ptr);
        });
    }
}

// ---------------------------------------------------------------------------
// Unit tests in Rust
// ---------------------------------------------------------------------------
#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    #[test]
    fn test_blake3_hashing_matches_expected() {
        let temp_dir = std::env::temp_dir();
        let file_path = temp_dir.join("test_blake3_hashing.txt");
        {
            let mut f = File::create(&file_path).unwrap();
            f.write_all(b"Hello Google Drive Work Sync Rust SIMD!").unwrap();
        }

        let hash_bytes = hash_file_blake3(&file_path, CancelToken(std::ptr::null())).unwrap();
        let expected = blake3::hash(b"Hello Google Drive Work Sync Rust SIMD!");
        assert_eq!(hash_bytes, *expected.as_bytes());

        let _ = std::fs::remove_file(&file_path);
    }

    #[test]
    fn test_header_hash_truncation() {
        let temp_dir = std::env::temp_dir();
        let file_path = temp_dir.join("test_blake3_header.txt");
        {
            let mut f = File::create(&file_path).unwrap();
            f.write_all(&[1u8; 10000]).unwrap();
        }

        let (hash_bytes, bytes_read) = hash_file_header_blake3(&file_path, 1000, CancelToken(std::ptr::null())).unwrap();
        assert_eq!(bytes_read, 1000);
        let expected = blake3::hash(&[1u8; 1000]);
        assert_eq!(hash_bytes, *expected.as_bytes());

        let _ = std::fs::remove_file(&file_path);
    }
}
