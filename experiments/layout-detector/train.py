"""Train TinyLayoutYOLO on the YOLO-DLA dataset.

Defaults are calibrated for an 8GB GPU at 480×480 input. Adjust --batch-size
or --input-size for other hardware. Resumes from the latest checkpoint in
the output directory if --resume is set.

Example:
    python train.py \\
        --images /home/stefan/Downloads/18000/images \\
        --labels /home/stefan/Downloads/18000/labels \\
        --output runs/v1 \\
        --epochs 50 --batch-size 8
"""

from __future__ import annotations

import argparse
import json
import math
import shutil
import time
from pathlib import Path

import torch
import torch.nn as nn
from torch.amp import GradScaler, autocast
from torch.utils.data import DataLoader
from tqdm import tqdm

from layout_detector import (
    TinyLayoutYOLO, TinyYoloLoss, YoloDataset, build_model, yolo_collate,
)
from layout_detector.dataset import make_train_val_split, compute_class_weights
from layout_detector.model import warmstart_from_v1


def cosine_lr(step: int, total_steps: int, warmup_steps: int,
              base_lr: float, min_lr_ratio: float = 0.01) -> float:
    if step < warmup_steps:
        return base_lr * (step + 1) / max(1, warmup_steps)
    progress = (step - warmup_steps) / max(1, total_steps - warmup_steps)
    progress = min(progress, 1.0)
    return base_lr * (min_lr_ratio + (1 - min_lr_ratio) * 0.5 * (1 + math.cos(math.pi * progress)))


def train_one_epoch(model: nn.Module, loss_fn: TinyYoloLoss,
                    loader: DataLoader, optimizer: torch.optim.Optimizer,
                    scaler: GradScaler, device: torch.device,
                    step_counter: list[int], total_steps: int,
                    warmup_steps: int, base_lr: float,
                    use_amp: bool, grad_clip: float,
                    progress_desc: str) -> dict:
    model.train()
    sums = {"total": 0.0, "cls": 0.0, "obj": 0.0, "reg": 0.0}
    n_batches = 0

    pbar = tqdm(loader, desc=progress_desc, leave=False)
    for images, labels in pbar:
        # Per-step LR (cosine w/ warmup)
        lr = cosine_lr(step_counter[0], total_steps, warmup_steps, base_lr)
        for g in optimizer.param_groups:
            g["lr"] = lr

        images = images.to(device, non_blocking=True)
        optimizer.zero_grad(set_to_none=True)

        with autocast(device_type="cuda", enabled=use_amp):
            out = model(images)
        # Loss runs in fp32 (it upcasts internally) — pixel-area math
        # overflows fp16 at 480² resolution.
        loss_dict = loss_fn(out, labels)
        loss = loss_dict["total"]

        if use_amp:
            scaler.scale(loss).backward()
            if grad_clip > 0:
                scaler.unscale_(optimizer)
                nn.utils.clip_grad_norm_(model.parameters(), grad_clip)
            scaler.step(optimizer)
            scaler.update()
        else:
            loss.backward()
            if grad_clip > 0:
                nn.utils.clip_grad_norm_(model.parameters(), grad_clip)
            optimizer.step()

        step_counter[0] += 1
        for k in sums:
            sums[k] += float(loss_dict[k])
        n_batches += 1

        pbar.set_postfix(
            loss=f"{sums['total']/n_batches:.3f}",
            cls=f"{sums['cls']/n_batches:.3f}",
            obj=f"{sums['obj']/n_batches:.3f}",
            reg=f"{sums['reg']/n_batches:.3f}",
            lr=f"{lr:.2e}",
        )

    return {k: v / max(1, n_batches) for k, v in sums.items()}


@torch.no_grad()
def evaluate(model: nn.Module, loss_fn: TinyYoloLoss,
             loader: DataLoader, device: torch.device, use_amp: bool) -> dict:
    model.eval()
    sums = {"total": 0.0, "cls": 0.0, "obj": 0.0, "reg": 0.0}
    n_batches = 0
    for images, labels in loader:
        images = images.to(device, non_blocking=True)
        with autocast(device_type="cuda", enabled=use_amp):
            out = model(images)
        loss_dict = loss_fn(out, labels)
        for k in sums:
            sums[k] += float(loss_dict[k])
        n_batches += 1
    return {k: v / max(1, n_batches) for k, v in sums.items()}


def save_checkpoint(path: Path, model: nn.Module, optimizer, scaler,
                    epoch: int, step: int, metrics: dict, args: argparse.Namespace) -> None:
    payload = {
        "model": model.state_dict(),
        "optimizer": optimizer.state_dict(),
        "scaler": scaler.state_dict() if scaler is not None else None,
        "epoch": epoch,
        "step": step,
        "metrics": metrics,
        "args": vars(args),
    }
    torch.save(payload, path)


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--images", type=Path, required=True)
    p.add_argument("--labels", type=Path, required=True)
    p.add_argument("--output", type=Path, default=Path("runs/v1"))
    p.add_argument("--num-classes", type=int, default=16)
    p.add_argument("--input-size", type=int, default=480)
    p.add_argument("--batch-size", type=int, default=8)
    p.add_argument("--epochs", type=int, default=50)
    p.add_argument("--workers", type=int, default=4)
    p.add_argument("--lr", type=float, default=1e-3)
    p.add_argument("--weight-decay", type=float, default=5e-4)
    p.add_argument("--warmup-epochs", type=int, default=3)
    p.add_argument("--val-fraction", type=float, default=0.05)
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--amp", action="store_true", default=True)
    p.add_argument("--no-amp", action="store_false", dest="amp")
    p.add_argument("--grad-clip", type=float, default=5.0)
    p.add_argument("--no-pretrained", action="store_false", dest="pretrained",
                   help="Train from scratch (no ImageNet backbone init)")
    p.add_argument("--pretrained", action="store_true", default=True)
    p.add_argument("--resume", action="store_true",
                   help="Resume from <output>/last.pt if present (full state)")
    p.add_argument("--warmstart", type=Path, default=None,
                   help="Warm-start from a v1 (single-level) checkpoint. Loads "
                        "backbone + copies head weights into both v2 heads. "
                        "FPN initialises fresh. Fresh optimizer state.")
    p.add_argument("--class-weights", type=str, default="auto",
                   help="'auto' (default) computes inverse-frequency-sqrt weights "
                        "from the label dir. 'uniform' uses 1.0 for all classes.")
    args = p.parse_args()

    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "args.json").write_text(json.dumps(vars(args), default=str, indent=2))

    torch.manual_seed(args.seed)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"device: {device}")

    # Data
    train_files, val_files = make_train_val_split(
        args.images, args.labels, val_fraction=args.val_fraction, seed=args.seed,
    )
    print(f"train: {len(train_files)}  val: {len(val_files)}")

    train_ds = YoloDataset(args.images, args.labels, args.input_size, train_files)
    val_ds = YoloDataset(args.images, args.labels, args.input_size, val_files)

    train_loader = DataLoader(
        train_ds, batch_size=args.batch_size, shuffle=True, drop_last=True,
        num_workers=args.workers, collate_fn=yolo_collate, pin_memory=True,
        persistent_workers=args.workers > 0,
    )
    val_loader = DataLoader(
        val_ds, batch_size=args.batch_size, shuffle=False, drop_last=False,
        num_workers=max(1, args.workers // 2), collate_fn=yolo_collate, pin_memory=True,
        persistent_workers=args.workers > 0,
    )

    # Model + loss + optim
    model = build_model(num_classes=args.num_classes, pretrained=args.pretrained).to(device)
    n_params = sum(p.numel() for p in model.parameters() if p.requires_grad)
    print(f"params: {n_params / 1e6:.2f}M")

    # Per-class focal-loss weights
    if args.class_weights == "uniform":
        cw_list = None
        print("class weights: uniform (disabled)")
    else:
        cw_list, counts = compute_class_weights(args.labels, args.num_classes)
        print("class weights (inverse-frequency-sqrt, clamped to [0.3, 10.0]):")
        names = ["t", "t1", "t2", "t3", "paragraph", "author", "keyword",
                 "abstract", "reference", "graph", "note", "other", "formula",
                 "table", "footnote", "class17"]
        for i, (n, c, w) in enumerate(zip(names[: args.num_classes], counts, cw_list)):
            print(f"  {i:>2} {n:<10}  count={c:>7}  weight={w:.3f}")
    loss_fn = TinyYoloLoss(num_classes=args.num_classes,
                           input_size=args.input_size, strides=model.strides,
                           class_weights=cw_list)
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr,
                                  weight_decay=args.weight_decay)
    scaler = GradScaler("cuda", enabled=args.amp)

    steps_per_epoch = len(train_loader)
    total_steps = steps_per_epoch * args.epochs
    warmup_steps = steps_per_epoch * args.warmup_epochs
    step_counter = [0]
    start_epoch = 0
    best_val = float("inf")

    last_path = args.output / "last.pt"
    best_path = args.output / "best.pt"
    if args.resume and last_path.exists():
        print(f"resuming from {last_path}")
        ck = torch.load(last_path, map_location=device, weights_only=False)
        model.load_state_dict(ck["model"])
        optimizer.load_state_dict(ck["optimizer"])
        if ck.get("scaler") is not None:
            scaler.load_state_dict(ck["scaler"])
        start_epoch = ck["epoch"] + 1
        step_counter[0] = ck["step"]
        best_val = ck["metrics"].get("best_val", float("inf"))
    elif args.warmstart is not None:
        print(f"warm-starting from checkpoint: {args.warmstart}")
        prior_ck = torch.load(args.warmstart, map_location=device, weights_only=False)
        prior_state = (prior_ck["model"] if isinstance(prior_ck, dict) and "model" in prior_ck
                       else prior_ck)
        loaded = warmstart_from_v1(model, prior_state)
        print(f"  direct match: {loaded['direct']}  "
              f"v1-head→p3: {loaded['head_p3_v1']}  "
              f"v1-head→p4: {loaded['head_p4_v1']}  "
              f"fresh (random init): {loaded['fresh']}  "
              f"skipped (no fit): {loaded['skipped_shape']}")
        print("  optimizer state: FRESH (warm-start drops optimiser buffers)")

    history = []
    for epoch in range(start_epoch, args.epochs):
        t0 = time.time()
        train_metrics = train_one_epoch(
            model, loss_fn, train_loader, optimizer, scaler, device,
            step_counter, total_steps, warmup_steps, args.lr,
            args.amp, args.grad_clip,
            progress_desc=f"epoch {epoch + 1}/{args.epochs}",
        )
        val_metrics = evaluate(model, loss_fn, val_loader, device, args.amp)
        dt = time.time() - t0

        is_best = val_metrics["total"] < best_val
        if is_best:
            best_val = val_metrics["total"]
        record = {
            "epoch": epoch + 1,
            "time_s": dt,
            "train": train_metrics,
            "val": val_metrics,
            "best_val": best_val,
        }
        history.append(record)
        (args.output / "history.json").write_text(json.dumps(history, indent=2))

        print(f"epoch {epoch + 1}/{args.epochs}  ({dt/60:.1f}min)  "
              f"train total={train_metrics['total']:.3f} obj={train_metrics['obj']:.3f} "
              f"cls={train_metrics['cls']:.3f} reg={train_metrics['reg']:.3f}  ||  "
              f"val total={val_metrics['total']:.3f}  best={best_val:.3f}"
              + ("  ★ NEW BEST" if is_best else ""))

        save_checkpoint(last_path, model, optimizer, scaler, epoch,
                        step_counter[0], record, args)
        if is_best:
            shutil.copy2(last_path, best_path)

    print(f"done. best val total = {best_val:.4f}")
    print(f"checkpoints in: {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
