"""Palette-aware styling for the graphical setup surface."""


def stylesheet(dark, checkmark):
    colors = (dict(background="#17191c", surface="#22252a", text="#f2f4f6", muted="#a5acb6",
                   border="#383e46", hover="#2c3239", accent="#70d9a3", selected="#223b30",
                   button="#a0edc2", button_text="#12261b", warning="#382e1d", warning_text="#f0ce8a")
              if dark else
              dict(background="#f7f8fa", surface="#ffffff", text="#20252b", muted="#66717d",
                   border="#dce1e7", hover="#eef1f5", accent="#207c50", selected="#edf8f1",
                   button="#237d51", button_text="#ffffff", warning="#fff6e5", warning_text="#805b19"))
    return """
        QWidget {{ color: {text}; font-size: 14px; }}
        QMainWindow, QWidget#page {{ background: {background}; }}
        QScrollArea, QWidget#scroll-content {{ background: transparent; border: none; }}
        QLabel {{ background: transparent; }}
        QLabel#brand {{ font-size: 16px; font-weight: 600; }}
        QLabel#eyebrow {{ color: {muted}; font-size: 12px; }}
        QLabel#heading {{ font-size: 28px; font-weight: 650; }}
        QLabel#muted, QLabel#section-label {{ color: {muted}; }}
        QLabel#section-label {{ font-size: 12px; font-weight: 600; }}
        QLabel#badge {{ background: {selected}; color: {accent}; border-radius: 9px;
                         padding: 5px 10px; font-size: 11px; font-weight: 600; }}
        QFrame#integration {{ background: {surface}; border: 1px solid {border}; border-radius: 12px; }}
        QLabel#integration-title {{ font-weight: 600; font-size: 16px; }}
        QLabel#warning {{ background: {warning}; color: {warning_text}; padding: 12px; border-radius: 8px; }}
        QLabel#notice {{ color: {muted}; font-size: 12px; }}
        QPushButton {{ background: {surface}; border: 1px solid {border}; border-radius: 8px;
                       padding: 9px 16px; font-weight: 500; }}
        QPushButton:hover {{ background: {hover}; }}
        QPushButton:focus {{ border: 2px solid {accent}; }}
        QPushButton#desktop-card {{ text-align: left; padding: 14px 16px; font-size: 14px;
                                   border-radius: 10px; }}
        QPushButton#desktop-card:checked {{ border: 2px solid {accent}; background: {selected}; }}
        QPushButton#install-button {{ background: {button}; color: {button_text}; border: 2px solid {button};
                                     font-weight: 600; padding: 10px 22px; }}
        QPushButton#install-button:hover {{ background: {accent}; }}
        QPushButton:disabled {{ background: {hover}; color: {muted}; border-color: {border}; }}
        QPushButton#install-button:disabled {{ background: {hover}; color: {muted}; border-color: {border}; }}
        QPushButton#text-button {{ border: none; background: transparent; color: {muted}; padding: 6px 0; }}
        QPushButton#text-button:hover {{ color: {accent}; }}
        QCheckBox {{ spacing: 10px; background: transparent; }}
        QCheckBox::indicator {{ width: 18px; height: 18px; border: 1px solid {border};
                               border-radius: 4px; background: {surface}; }}
        QCheckBox::indicator:hover {{ border-color: {accent}; }}
        QCheckBox::indicator:checked {{ background: #237d51; border-color: #237d51; image: url("{checkmark}"); }}
        QCheckBox:focus {{ color: {accent}; }}
        QLineEdit, QPlainTextEdit {{ background: {surface}; color: {text}; border: 1px solid {border};
                                   border-radius: 7px; padding: 8px; selection-background-color: {selected}; }}
        QLineEdit:focus {{ border: 1px solid {accent}; }}
        QProgressBar {{ border: none; background: {border}; border-radius: 2px; max-height: 4px; }}
        QProgressBar::chunk {{ background: {accent}; border-radius: 2px; }}
        QFrame#footer {{ border-top: 1px solid {border}; background: {surface}; }}
    """.format(**colors, checkmark=str(checkmark).replace("\\", "/").replace('"', '\\"'))
