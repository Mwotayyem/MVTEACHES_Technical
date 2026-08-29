from pathlib import Path

ROOT = Path(__file__).resolve().parent
SOURCE = ROOT / "index.html"
PAGES = [
    "portal_dashboard.html",
    "portal_classes.html",
    "portal_attendance.html",
    "portal_apology.html",
    "portal_finance.html",
    "portal_profile.html",
    "portal_progress.html",
    "portal_test.html",
]


def main() -> None:
    content = SOURCE.read_text(encoding="utf-8")
    for page in PAGES:
        (ROOT / page).write_text(content, encoding="utf-8", newline="\n")
    print(f"Updated {len(PAGES)} portal pages from index.html")


if __name__ == "__main__":
    main()
