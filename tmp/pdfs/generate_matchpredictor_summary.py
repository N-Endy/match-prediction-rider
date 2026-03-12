from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.platypus import ListFlowable, ListItem, Paragraph, SimpleDocTemplate, Spacer, Table, TableStyle


ROOT = Path("/Users/nnamdi/Desktop/Projects/MatchPredictor/MatchPredictor")
OUTPUT = ROOT / "output/pdf/matchpredictor-app-summary.pdf"


def build_styles():
    styles = getSampleStyleSheet()
    return {
        "title": ParagraphStyle(
            "Title",
            parent=styles["Heading1"],
            fontName="Helvetica-Bold",
            fontSize=18,
            leading=21,
            textColor=colors.HexColor("#0f172a"),
            spaceAfter=5,
        ),
        "subtitle": ParagraphStyle(
            "Subtitle",
            parent=styles["BodyText"],
            fontName="Helvetica",
            fontSize=8.5,
            leading=10,
            textColor=colors.HexColor("#475569"),
            spaceAfter=8,
        ),
        "heading": ParagraphStyle(
            "Heading",
            parent=styles["Heading2"],
            fontName="Helvetica-Bold",
            fontSize=10,
            leading=12,
            textColor=colors.HexColor("#0f172a"),
            spaceBefore=4,
            spaceAfter=3,
        ),
        "body": ParagraphStyle(
            "Body",
            parent=styles["BodyText"],
            fontName="Helvetica",
            fontSize=8.2,
            leading=10,
            alignment=TA_LEFT,
            textColor=colors.HexColor("#1f2937"),
            spaceAfter=2,
        ),
        "bullet": ParagraphStyle(
            "Bullet",
            parent=styles["BodyText"],
            fontName="Helvetica",
            fontSize=8.0,
            leading=9.5,
            leftIndent=0,
            textColor=colors.HexColor("#1f2937"),
        ),
        "small": ParagraphStyle(
            "Small",
            parent=styles["BodyText"],
            fontName="Helvetica",
            fontSize=7.4,
            leading=9,
            textColor=colors.HexColor("#475569"),
            spaceAfter=0,
        ),
    }


def bullet_list(items, style):
    return ListFlowable(
        [
            ListItem(Paragraph(item, style), leftIndent=0)
            for item in items
        ],
        bulletType="bullet",
        start="circle",
        leftIndent=10,
        bulletFontName="Helvetica",
        bulletFontSize=7,
        spaceBefore=0,
        spaceAfter=0,
    )


def section(title, content, heading_style):
    return [
        Paragraph(title, heading_style),
        content,
    ]


def build_pdf():
    styles = build_styles()
    doc = SimpleDocTemplate(
        str(OUTPUT),
        pagesize=A4,
        leftMargin=14 * mm,
        rightMargin=14 * mm,
        topMargin=12 * mm,
        bottomMargin=10 * mm,
        title="MatchPredictor App Summary",
        author="OpenAI Codex",
    )

    what_it_is = Paragraph(
        "MatchPredictor is an ASP.NET Core web app for daily football prediction workflows. "
        "Repo evidence shows it scrapes match data, stores it in PostgreSQL, runs scheduled analysis jobs, "
        "and publishes prediction pages, analytics, value bets, booking, and AI chat features.",
        styles["body"],
    )

    who_its_for = Paragraph(
        "Primary user/persona: football bettors or tip-followers who want daily match picks and related tools. "
        "Specific target market or user research: <b>Not found in repo.</b>",
        styles["body"],
    )

    feature_items = [
        "Daily prediction pages for BTTS, Over 2.5, Straight Win, Draw, and Combined picks.",
        "Scheduled scraping and prediction generation with Hangfire recurring jobs.",
        "Score updates every 5 minutes plus prediction/result reconciliation.",
        "Analytics page with today, yesterday, 3-day, and 7-day accuracy stats.",
        "Value bets API and page backed by a dedicated value-bets service.",
        "AI chat endpoint and protected page using a configured Groq model.",
        "SportyBet booking API for selected games and a Hangfire dashboard for jobs.",
    ]

    architecture_lines = [
        "<b>Web:</b> `MatchPredictor.Web` serves Razor Pages, API controllers, static assets, `/health`, and `/hangfire`.",
        "<b>Application:</b> `AnalyzerService` orchestrates scraping, database sync, prediction generation, score updates, cleanup, and daily analysis.",
        "<b>Infrastructure:</b> EF Core `ApplicationDbContext`, repositories, scraper services, probability/regression services, and external HTTP clients.",
        "<b>Data/services:</b> PostgreSQL is the main store; Redis cache is optional with in-memory fallback; Groq and SportyBet HTTP clients are registered.",
        "<b>Flow:</b> recurring jobs trigger scrape -> extract Excel -> save `MatchData` -> generate/save `Prediction` -> update scores/results -> expose pages/APIs.",
    ]

    getting_started = [
        "Set `ConnectionStrings__DefaultConnection` to a PostgreSQL connection string; optional config includes `ConnectionStrings__RedisConnection`, `GroqApiKey`, `GroqModel`, `AiChatPassword`, and Hangfire credentials.",
        "Local dev: run `dotnet run --project MatchPredictor.Web` and open `http://localhost:5228` (from launch settings).",
        "Container path in repo: run `./start.sh` or `docker-compose up --build`, then open `http://localhost:8080`.",
        "On startup the app applies EF Core migrations automatically and registers recurring Hangfire jobs.",
    ]

    left_column = []
    left_column.extend(section("What It Is", what_it_is, styles["heading"]))
    left_column.append(Spacer(1, 2))
    left_column.extend(section("Who It's For", who_its_for, styles["heading"]))
    left_column.append(Spacer(1, 2))
    left_column.extend(section("What It Does", bullet_list(feature_items, styles["bullet"]), styles["heading"]))

    right_column = []
    right_column.extend(section(
        "How It Works",
        bullet_list(architecture_lines, styles["bullet"]),
        styles["heading"],
    ))
    right_column.append(Spacer(1, 2))
    right_column.extend(section(
        "How To Run",
        bullet_list(getting_started, styles["bullet"]),
        styles["heading"],
    ))
    right_column.append(Spacer(1, 3))
    right_column.append(Paragraph("Unknowns marked directly in the summary are based on explicit repo gaps.", styles["small"]))

    table = Table(
        [[left_column, right_column]],
        colWidths=[88 * mm, 88 * mm],
        hAlign="LEFT",
    )
    table.setStyle(
        TableStyle(
            [
                ("VALIGN", (0, 0), (-1, -1), "TOP"),
                ("LEFTPADDING", (0, 0), (-1, -1), 0),
                ("RIGHTPADDING", (0, 0), (-1, -1), 8),
                ("TOPPADDING", (0, 0), (-1, -1), 0),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 0),
                ("LINEAFTER", (0, 0), (0, 0), 0.5, colors.HexColor("#cbd5e1")),
            ]
        )
    )

    story = [
        Paragraph("MatchPredictor", styles["title"]),
        Paragraph("One-page repo summary generated from source evidence only", styles["subtitle"]),
        table,
    ]

    doc.build(story)


if __name__ == "__main__":
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    build_pdf()
