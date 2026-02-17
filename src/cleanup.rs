use std::path::PathBuf;

pub struct CleanupReport {
    pub files_removed: usize,
    pub bytes_freed: u64,
}

impl std::fmt::Display for CleanupReport {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        if self.files_removed == 0 {
            write!(f, "Nothing to clean up.")
        } else {
            let kb = self.bytes_freed as f64 / 1024.0;
            write!(
                f,
                "Removed {} file(s), freed {:.1} KB.",
                self.files_removed, kb
            )
        }
    }
}

pub fn run_cleanup() -> CleanupReport {
    let mut report = CleanupReport {
        files_removed: 0,
        bytes_freed: 0,
    };

    let cwd = std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."));

    // Remove cache/ directory contents
    let cache_dir = cwd.join("cache");
    if cache_dir.is_dir() {
        clean_directory(&cache_dir, &mut report);
    }

    // Remove *.tmp files (orphaned analysis artifacts)
    remove_matching_files(&cwd, "tmp", None, &mut report);

    // Remove *.log files older than 7 days
    remove_matching_files(&cwd, "log", Some(7), &mut report);

    if report.files_removed > 0 {
        log::info!(
            "Cleanup: removed {} files, freed {} bytes",
            report.files_removed,
            report.bytes_freed
        );
    }

    report
}

fn clean_directory(dir: &PathBuf, report: &mut CleanupReport) {
    let entries = match std::fs::read_dir(dir) {
        Ok(e) => e,
        Err(_) => return,
    };

    for entry in entries.flatten() {
        let path = entry.path();
        let name = path.file_name().unwrap_or_default().to_string_lossy();

        // Skip safety-critical files
        if name == "config.json" || name.ends_with(".lock") || name.ends_with(".onnx") {
            continue;
        }

        if path.is_file() {
            if let Ok(meta) = std::fs::metadata(&path) {
                report.bytes_freed += meta.len();
            }
            if std::fs::remove_file(&path).is_ok() {
                report.files_removed += 1;
            }
        } else if path.is_dir() {
            clean_directory(&path, report);
            // Try to remove the now-empty directory
            let _ = std::fs::remove_dir(&path);
        }
    }
}

fn remove_matching_files(
    dir: &PathBuf,
    extension: &str,
    max_age_days: Option<u64>,
    report: &mut CleanupReport,
) {
    let entries = match std::fs::read_dir(dir) {
        Ok(e) => e,
        Err(_) => return,
    };

    let now = std::time::SystemTime::now();

    for entry in entries.flatten() {
        let path = entry.path();
        if !path.is_file() {
            continue;
        }

        let matches_ext = path.extension().map(|e| e == extension).unwrap_or(false);

        if !matches_ext {
            continue;
        }

        // Skip safety-critical files
        let name = path.file_name().unwrap_or_default().to_string_lossy();
        if name == "config.json" || name.ends_with(".lock") || name.ends_with(".onnx") {
            continue;
        }

        if let Some(max_days) = max_age_days {
            let old_enough = std::fs::metadata(&path)
                .and_then(|m| m.modified())
                .map(|modified| {
                    now.duration_since(modified)
                        .map(|d| d.as_secs() > max_days * 86400)
                        .unwrap_or(false)
                })
                .unwrap_or(false);

            if !old_enough {
                continue;
            }
        }

        if let Ok(meta) = std::fs::metadata(&path) {
            report.bytes_freed += meta.len();
        }
        if std::fs::remove_file(&path).is_ok() {
            report.files_removed += 1;
        }
    }
}
