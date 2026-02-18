pub fn show_shortcuts_window(ctx: &egui::Context, show: &mut bool) {
    egui::Window::new("Keyboard Shortcuts")
        .open(show)
        .resizable(true)
        .collapsible(false)
        .default_width(380.0)
        .default_height(500.0)
        .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
        .show(ctx, |ui| {
            egui::ScrollArea::vertical()
                .auto_shrink(false)
                .show(ui, |ui| {
                    let sections: &[(&str, &[(&str, &str)])] = &[
                        (
                            "General",
                            &[
                                ("Ctrl+O", "Open file"),
                                ("Ctrl+W", "Close tab"),
                                ("Ctrl+Q", "Quit"),
                                ("Ctrl+Tab", "Next tab"),
                                ("Escape", "Quit"),
                                ("F1", "Toggle this dialog"),
                            ][..],
                        ),
                        (
                            "Navigation",
                            &[
                                ("PgDn", "Next page"),
                                ("PgUp", "Previous page"),
                                ("Home", "First page"),
                                ("End", "Last page"),
                            ][..],
                        ),
                        (
                            "View",
                            &[
                                ("+ / =", "Zoom in"),
                                ("-", "Zoom out"),
                                ("0", "Fit page"),
                                ("Shift+D", "Toggle debug overlay"),
                            ][..],
                        ),
                        (
                            "Rail Mode (active above zoom threshold)",
                            &[
                                ("Down / S", "Next line"),
                                ("Up / W", "Previous line"),
                                ("Right / D", "Scroll forward"),
                                ("Left / A", "Scroll backward"),
                                ("Click on block", "Jump to block"),
                            ][..],
                        ),
                        (
                            "Pan (below zoom threshold)",
                            &[
                                ("Arrow keys / WASD", "Pan"),
                                ("Click + drag", "Pan"),
                            ][..],
                        ),
                    ];

                    for (i, (heading, shortcuts)) in sections.iter().enumerate() {
                        if i > 0 {
                            ui.add_space(4.0);
                        }
                        ui.heading(*heading);
                        ui.add_space(2.0);
                        egui::Grid::new(format!("shortcuts_{i}"))
                            .num_columns(2)
                            .spacing([20.0, 4.0])
                            .show(ui, |ui| {
                                for (key, desc) in *shortcuts {
                                    ui.label(
                                        egui::RichText::new(*key).monospace().strong(),
                                    );
                                    ui.label(*desc);
                                    ui.end_row();
                                }
                            });
                    }
                });
        });
}
