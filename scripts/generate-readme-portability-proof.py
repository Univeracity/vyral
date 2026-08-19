#!/usr/bin/env python3
"""Generate the README portability proof from recorded qualification state.

Requires Pillow 10 or newer. The output is an animated PNG plus a static
reduced-motion fallback; neither image makes a provider claim that is absent
from qualification/adapter-qualification.json.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import tempfile

from PIL import Image, ImageDraw, ImageFilter, ImageFont, PngImagePlugin


ROOT = Path(__file__).resolve().parent.parent
QUALIFICATION = ROOT / "qualification" / "adapter-qualification.json"
EXECUTION_FIXTURE = (
    ROOT / "conformance" / "runtime" / "v1" / "scenarios" / "execution" / "native-lifecycle.json"
)
LOGO = ROOT / "docs" / "assets" / "vyral-logo-50.png"
ANIMATED = ROOT / "docs" / "assets" / "vyral-portability-proof.png"
STATIC = ROOT / "docs" / "assets" / "vyral-portability-proof-static.png"

WIDTH = 1400
HEIGHT = 780

INK = "#eef7fc"
MUTED = "#7894a8"
FAINT = "#4f6b7e"
CYAN = "#70bff3"
CYAN_STRONG = "#00a6ff"
LINE = "#18354a"
PANEL = "#07111b"
CARD = "#0a1723"
BLACK = "#03070c"

EXPECTED = (
    "local-sqlite",
    "aws-dynamodb-sqs",
    "azure-durable",
    "google-firestore-cloud-tasks",
    "temporal",
)

DISPLAY_NAMES = {
    "local-sqlite": "Local · SQLite",
    "aws-dynamodb-sqs": "AWS · DynamoDB + SQS",
    "azure-durable": "Azure · Durable Functions",
    "google-firestore-cloud-tasks": "Google · Firestore + Tasks",
    "temporal": "Temporal",
}

ALLOWED_LEVELS = {
    "local-sqlite": "local_conformant",
    "aws-dynamodb-sqs": "live_qualified",
    "azure-durable": "live_qualified",
    "google-firestore-cloud-tasks": "live_qualified",
    "temporal": "prototype",
}

SURFACES = ("records", "retrieval", "execution", "agents")
TAB_LABELS = {
    "records": "Records",
    "retrieval": "Retrieval",
    "execution": "Execution",
    "agents": "Agent access",
}


def _font_path(*candidates: str) -> str:
    roots = (
        Path("/usr/share/fonts/truetype/dejavu"),
        Path("/usr/share/fonts/opentype/noto"),
        Path("/usr/share/fonts/truetype/liberation2"),
    )
    for root in roots:
        for candidate in candidates:
            path = root / candidate
            if path.exists():
                return str(path)
    raise RuntimeError(f"No suitable font found for: {', '.join(candidates)}")


SANS = _font_path("DejaVuSans.ttf", "NotoSans-Regular.ttf", "LiberationSans-Regular.ttf")
SANS_BOLD = _font_path("DejaVuSans-Bold.ttf", "NotoSans-Bold.ttf", "LiberationSans-Bold.ttf")
MONO = _font_path("DejaVuSansMono.ttf", "NotoSansMono-Regular.ttf", "LiberationMono-Regular.ttf")
MONO_BOLD = _font_path("DejaVuSansMono-Bold.ttf", "NotoSansMono-Bold.ttf", "LiberationMono-Bold.ttf")


def font(size: int, *, bold: bool = False, mono: bool = False) -> ImageFont.FreeTypeFont:
    path = MONO_BOLD if mono and bold else MONO if mono else SANS_BOLD if bold else SANS
    return ImageFont.truetype(path, size)


def rgb(value: str) -> tuple[int, int, int]:
    value = value.lstrip("#")
    return tuple(int(value[index:index + 2], 16) for index in (0, 2, 4))  # type: ignore[return-value]


def gradient_background() -> Image.Image:
    top = rgb("#08121d")
    bottom = rgb(BLACK)
    image = Image.new("RGB", (WIDTH, HEIGHT))
    pixels = image.load()
    for y in range(HEIGHT):
        mix = y / max(HEIGHT - 1, 1)
        row = tuple(round(top[index] * (1 - mix) + bottom[index] * mix) for index in range(3))
        for x in range(WIDTH):
            pixels[x, y] = row

    glow = Image.new("RGBA", image.size, (0, 0, 0, 0))
    glow_draw = ImageDraw.Draw(glow)
    glow_draw.ellipse((440, 90, 1060, 710), fill=(0, 135, 230, 36))
    glow = glow.filter(ImageFilter.GaussianBlur(110))
    return Image.alpha_composite(image.convert("RGBA"), glow)


def load_providers() -> list[dict[str, str]]:
    payload = json.loads(QUALIFICATION.read_text(encoding="utf-8"))
    by_id = {adapter["adapterId"]: adapter for adapter in payload["adapters"]}
    providers: list[dict[str, str]] = []
    for adapter_id in EXPECTED:
        if adapter_id not in by_id:
            raise RuntimeError(f"Qualification data does not contain {adapter_id}")
        adapter = by_id[adapter_id]
        qualification = adapter["qualification"]
        level = qualification["level"]
        if qualification["status"] != "current" or level != ALLOWED_LEVELS[adapter_id]:
            raise RuntimeError(
                f"Refusing to advertise {adapter_id}: expected current "
                f"{ALLOWED_LEVELS[adapter_id]}, found {qualification['status']} {level}"
            )
        providers.append(
            {
                "id": adapter_id,
                "name": DISPLAY_NAMES[adapter_id],
                "level": level.replace("_", " ").upper(),
            }
        )
    return providers


def load_contract_example() -> dict[str, str]:
    payload = json.loads(EXECUTION_FIXTURE.read_text(encoding="utf-8"))
    step = next(
        item
        for item in payload["steps"]
        if item["id"] == "native-success-idempotency-and-owned-state"
    )
    request = step["arguments"]["request"]
    return {
        "handlerId": request["handlerId"],
        "idempotencyKey": request["idempotencyKey"],
    }


def text(draw: ImageDraw.ImageDraw, xy: tuple[int, int], value: str, *,
         size: int, color: str, bold: bool = False, mono: bool = False,
         anchor: str | None = None) -> None:
    draw.text(xy, value, fill=color, font=font(size, bold=bold, mono=mono), anchor=anchor)


def label(draw: ImageDraw.ImageDraw, xy: tuple[int, int], value: str) -> None:
    text(draw, xy, value.upper(), size=14, color=MUTED, bold=True, mono=True)


def rounded(draw: ImageDraw.ImageDraw, box: tuple[int, int, int, int], *,
            fill: str, outline: str = LINE, width: int = 1, radius: int = 18) -> None:
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)


def surface_content(
    surface: str,
    providers: list[dict[str, str]],
    example: dict[str, str],
) -> dict[str, object]:
    if surface == "records":
        return {
            "request": "POST /collections/{collection}/records",
            "detail": ('{ "id": "order-42", "partitionKey": "tenant-a",', '  "content": "..." }'),
            "result": "200 Committed",
            "result_detail": "record · tenant-a / order-42",
            "boundary": "atomic completion · stable etag",
            "traits": "identity · partition · optimistic concurrency",
            "choices": [{"name": "Local · SQLite", "level": "REFERENCE PATH"}],
        }
    if surface == "retrieval":
        return {
            "request": "POST /search",
            "detail": ('{ "query": "retention policy",', '  "mode": "lexical" }'),
            "result": "200 Ranked matches",
            "result_detail": "matches + citations",
            "boundary": "non-mutating retrieval",
            "traits": "bounded query · diagnostics · source identity",
            "choices": [{"name": "Local · SQLite", "level": "REFERENCE PATH"}],
        }
    if surface == "execution":
        return {
            "request": "POST /execution/runs",
            "detail": (
                f'{{ "handlerId": "{example["handlerId"]}",',
                f'  "idempotencyKey": "{example["idempotencyKey"]}" }}',
            ),
            "result": "202 Accepted",
            "result_detail": "/execution/runs/run_7f2a",
            "boundary": "receipt-bound durable admission",
            "traits": "stable identity · recoverable work · safe replay",
            "choices": providers,
        }
    if surface == "agents":
        return {
            "request": "POST /mcp",
            "detail": ("mcp-version: 2026-07-28", "vyral-route: retrieval"),
            "result": "200 Stateless response",
            "result_detail": "tools/call · retrieve",
            "boundary": "gateway-routable · no affinity",
            "traits": "self-describing · authorized · any healthy instance",
            "choices": [
                {"name": "Healthy instance 01", "level": "NO AFFINITY"},
                {"name": "Healthy instance 02", "level": "NO AFFINITY"},
                {"name": "Healthy instance 03", "level": "NO AFFINITY"},
            ],
        }
    raise ValueError(f"Unknown surface: {surface}")


def draw_frame(
    providers: list[dict[str, str]],
    example: dict[str, str],
    surface: str,
    active_choice: int,
) -> Image.Image:
    image = gradient_background()
    draw = ImageDraw.Draw(image)
    content = surface_content(surface, providers, example)
    choices = content["choices"]
    assert isinstance(choices, list)

    text(draw, (58, 38), "APPLICATION-OWNED CONTRACT PLANE", size=15, color=CYAN, bold=True, mono=True)
    text(draw, (58, 68), "Own the contract. Change what runs beneath it.", size=42, color=INK, bold=True)
    panel = (52, 158, 1348, 690)
    rounded(draw, panel, fill=PANEL, outline=LINE, width=2, radius=22)

    tab_width = (1348 - 52) // len(SURFACES)
    for index, tab in enumerate(SURFACES):
        left = 52 + index * tab_width
        right = 1348 if index == len(SURFACES) - 1 else left + tab_width
        selected = tab == surface
        draw.rectangle(
            (left + 1, 159, right - 1, 218),
            fill="#0b2131" if selected else "#07111b",
        )
        if index > 0:
            draw.line((left, 160, left, 218), fill=LINE, width=1)
        text(
            draw,
            ((left + right) // 2, 188),
            TAB_LABELS[tab],
            size=15,
            color=INK if selected else MUTED,
            bold=selected,
            mono=True,
            anchor="mm",
        )
        if selected:
            draw.line((left + 20, 217, right - 20, 217), fill=CYAN_STRONG, width=3)

    request = (80, 242, 708, 412)
    result = (80, 436, 708, 660)
    boundary = (736, 242, 886, 660)
    deployment = (914, 242, 1320, 660)
    rounded(draw, request, fill=CARD)
    rounded(draw, result, fill=CARD)
    rounded(draw, boundary, fill="#071b2a", outline="#1a5274")
    rounded(draw, deployment, fill="#06101a")

    label(draw, (108, 266), "Consumer request")
    text(draw, (108, 299), str(content["request"]), size=23, color=INK, bold=True, mono=True)
    rounded(draw, (108, 336, 680, 386), fill="#050c13", outline="#132b3d", radius=10)
    detail = content["detail"]
    assert isinstance(detail, tuple)
    text(draw, (128, 344), str(detail[0]), size=15, color="#99b4c7", mono=True)
    text(draw, (128, 365), str(detail[1]), size=15, color="#99b4c7", mono=True)

    label(draw, (108, 460), "Portable result")
    text(draw, (108, 494), str(content["result"]), size=26, color=INK, bold=True, mono=True)
    text(draw, (108, 537), str(content["result_detail"]), size=19, color=CYAN, bold=True, mono=True)
    text(draw, (108, 574), str(content["boundary"]), size=16, color="#99b4c7", mono=True)
    text(draw, (108, 618), str(content["traits"]), size=13, color=FAINT, mono=True)

    text(draw, (811, 277), "STABLE", size=13, color="#6c9bb8", bold=True, mono=True, anchor="mm")
    text(draw, (811, 298), "CONTRACT", size=13, color="#6c9bb8", bold=True, mono=True, anchor="mm")

    logo = Image.open(LOGO).convert("RGB").resize((100, 100), Image.Resampling.LANCZOS)
    mask = Image.new("L", logo.size, 255)
    image.paste(logo, (761, 348), mask)
    draw = ImageDraw.Draw(image)
    draw.line((811, 315, 811, 337), fill="#267da9", width=2)
    draw.line((811, 459, 811, 510), fill="#267da9", width=2)
    text(draw, (811, 542), "OWNED", size=14, color=CYAN, bold=True, mono=True, anchor="mm")
    text(draw, (811, 577), "one vocabulary", size=11, color=FAINT, mono=True, anchor="mm")
    text(draw, (811, 596), "at every seam", size=11, color=FAINT, mono=True, anchor="mm")

    label(draw, (942, 266), "Implementation path")
    choice_step = 65 if len(choices) >= 5 else 78
    choice_height = 54 if len(choices) >= 5 else 64
    for index, provider in enumerate(choices):
        y = 294 + index * choice_step
        selected = index == active_choice
        fill = "#0b2a3e" if selected else "#08141f"
        outline = CYAN_STRONG if selected else "#173247"
        rounded(
            draw,
            (942, y, 1292, y + choice_height),
            fill=fill,
            outline=outline,
            width=2 if selected else 1,
            radius=11,
        )
        text(draw, (962, y + 9), provider["name"], size=16, color=INK if selected else "#98afbf", bold=selected)
        text(
            draw,
            (962, y + 34),
            provider["level"],
            size=10,
            color=CYAN if selected else FAINT,
            bold=selected,
            mono=True,
        )
        if selected:
            dot_y = y + choice_height // 2
            draw.ellipse((1264, dot_y - 5, 1274, dot_y + 5), fill=CYAN_STRONG)

    footer_label = "THE SHAPE STAYS PUT" if surface == "execution" else "ONE CONTRACT PLANE"
    text(draw, (60, 728), footer_label, size=14, color=CYAN, bold=True, mono=True)
    text(
        draw,
        (270, 728),
        "Capability and adapter maturity remain explicit and evidence-scoped.",
        size=15,
        color=MUTED,
        mono=True,
    )
    text(
        draw,
        (1340, 728),
        "openvyral.com/#architecture  →",
        size=15,
        color="#9dd9fb",
        bold=True,
        mono=True,
        anchor="ra",
    )
    return image


def build(animated: Path, static: Path) -> None:
    providers = load_providers()
    example = load_contract_example()
    sequence = [
        ("records", 0, 1300),
        ("retrieval", 0, 1300),
        ("execution", 0, 1500),
        ("execution", 1, 1050),
        ("execution", 2, 1050),
        ("execution", 3, 1050),
        ("execution", 4, 1200),
        ("agents", 0, 1600),
    ]
    frames = [
        draw_frame(providers, example, surface, active)
        for surface, active, _duration in sequence
    ]
    durations = [duration for _surface, _active, duration in sequence]
    static_frame = draw_frame(providers, example, "execution", 0)
    metadata = PngImagePlugin.PngInfo()
    metadata.add_text("Description", "Vyral capability and execution portability proof")
    metadata.add_text(
        "Source",
        "qualification/adapter-qualification.json; "
        "conformance/runtime/v1/scenarios/execution/native-lifecycle.json",
    )

    static.parent.mkdir(parents=True, exist_ok=True)
    static_frame.save(static, format="PNG", optimize=True, pnginfo=metadata)
    frames[0].save(
        animated,
        format="PNG",
        save_all=True,
        append_images=frames[1:],
        duration=durations,
        loop=0,
        disposal=1,
        blend=0,
        optimize=True,
        pnginfo=metadata,
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="fail if committed images are stale")
    args = parser.parse_args()

    if not args.check:
        build(ANIMATED, STATIC)
        print(f"generated {ANIMATED.relative_to(ROOT)}")
        print(f"generated {STATIC.relative_to(ROOT)}")
        return 0

    with tempfile.TemporaryDirectory(prefix="vyral-portability-proof-") as directory:
        generated_animated = Path(directory) / ANIMATED.name
        generated_static = Path(directory) / STATIC.name
        build(generated_animated, generated_static)
        stale = []
        for expected, generated in (
            (ANIMATED, generated_animated),
            (STATIC, generated_static),
        ):
            if not expected.exists() or expected.read_bytes() != generated.read_bytes():
                stale.append(str(expected.relative_to(ROOT)))
        if stale:
            parser.error("stale generated asset(s): " + ", ".join(stale))
    print("README portability proof is current")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
