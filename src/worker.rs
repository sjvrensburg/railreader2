use std::collections::HashSet;
use std::sync::mpsc;
use std::thread;

use crate::layout::{self, AnalysisInput, PageAnalysis};

pub struct AnalysisRequest {
    pub page: i32,
    pub input: AnalysisInput,
}

pub struct AnalysisResult {
    pub page: i32,
    pub analysis: PageAnalysis,
}

pub struct AnalysisWorker {
    tx: mpsc::Sender<AnalysisRequest>,
    rx: mpsc::Receiver<AnalysisResult>,
    in_flight: HashSet<i32>,
}

impl AnalysisWorker {
    pub fn new(mut session: ort::session::Session) -> Self {
        let (req_tx, req_rx) = mpsc::channel::<AnalysisRequest>();
        let (res_tx, res_rx) = mpsc::channel::<AnalysisResult>();

        thread::Builder::new()
            .name("analysis-worker".into())
            .spawn(move || {
                while let Ok(request) = req_rx.recv() {
                    let page = request.page;
                    let page_w = request.input.page_w;
                    let page_h = request.input.page_h;
                    match layout::run_analysis(&mut session, request.input) {
                        Ok(analysis) => {
                            if res_tx.send(AnalysisResult { page, analysis }).is_err() {
                                break; // main thread dropped its receiver
                            }
                        }
                        Err(e) => {
                            log::warn!("Worker analysis failed for page {}: {}", page + 1, e);
                            // Send a fallback so the main thread knows this page is done
                            let fallback = layout::fallback_analysis(page_w, page_h);
                            if res_tx
                                .send(AnalysisResult {
                                    page,
                                    analysis: fallback,
                                })
                                .is_err()
                            {
                                break;
                            }
                        }
                    }
                }
                log::info!("Analysis worker thread exiting");
            })
            .expect("Failed to spawn analysis worker thread");

        Self {
            tx: req_tx,
            rx: res_rx,
            in_flight: HashSet::new(),
        }
    }

    /// Non-blocking submit. Returns false if the page is already in flight.
    pub fn submit(&mut self, request: AnalysisRequest) -> bool {
        if !self.in_flight.insert(request.page) {
            return false; // already in flight
        }
        self.tx.send(request).is_ok()
    }

    /// Non-blocking poll for completed results.
    pub fn poll(&mut self) -> Option<AnalysisResult> {
        match self.rx.try_recv() {
            Ok(result) => {
                self.in_flight.remove(&result.page);
                Some(result)
            }
            Err(_) => None,
        }
    }

    /// Check if a page is currently being analyzed.
    pub fn is_in_flight(&self, page: i32) -> bool {
        self.in_flight.contains(&page)
    }

    /// True if there are no pending requests.
    pub fn is_idle(&self) -> bool {
        self.in_flight.is_empty()
    }
}
