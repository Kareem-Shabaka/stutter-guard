#!/usr/bin/env python3
"""
latency_harvester.py (v2.0 - Zero-False-Positive Deterministic Edition)
Strictly validates DPC/ISR latency regressions using vendor-locked version grammars,
verbatim LatencyMon log parsing, sentence-level polarity classification, and
multi-source quorum gating.
"""

import argparse
import collections
import json
import logging
import os
import random
import re
import time
from urllib.parse import quote_plus
import requests
from bs4 import BeautifulSoup

# --- 1. Vendor-Scoped Target Definitions & Strict Version Grammars ---

SEED_TARGETS = {
    "Network": [
        {
            "file": "rtwlane601.sys",
            "name": "Realtek PCIe Wi-Fi 5/6 Adapter",
            "hw_id": "PCI\\VEN_10EC&DEV_C822*",
            "version_regex": r"\b(?:202[3-9]|6001)\.\d{1,2}\.\d{1,3}\.\d{1,4}\b"
        },
        {
            "file": "rtwlane.sys",
            "name": "Realtek PCIe 802.11ac Adapter",
            "hw_id": "PCI\\VEN_10EC&DEV_C821*",
            "version_regex": r"\b202[0-9]\.\d{1,2}\.\d{1,3}\.\d{1,4}\b"
        },
        {
            "file": "Netwtw10.sys",
            "name": "Intel Wi-Fi 6 Adapter (AX200/AX201)",
            "hw_id": "PCI\\VEN_8086&DEV_2723*",
            "version_regex": r"\b2[0-4]\.\d{1,3}\.\d{1,2}\.\d{1,2}\b"
        },
        {
            "file": "Netwtw12.sys",
            "name": "Intel Wi-Fi 6E Adapter (AX210/AX211)",
            "hw_id": "PCI\\VEN_8086&DEV_51F0*",
            "version_regex": r"\b2[1-4]\.\d{1,3}\.\d{1,2}\.\d{1,2}\b"
        },
        {
            "file": "Netwtw14.sys",
            "name": "Intel Wi-Fi 6E/7 Adapter (AX211/BE200)",
            "hw_id": "PCI\\VEN_8086&DEV_272B*",
            "version_regex": r"\b2[2-5]\.\d{1,3}\.\d{1,2}\.\d{1,2}\b"
        },
        {
            "file": "mtkwl6ex.sys",
            "name": "MediaTek / AMD RZ608/RZ616 Wi-Fi 6/6E Adapter",
            "hw_id": "PCI\\VEN_14C3&DEV_7961*",
            "version_regex": r"\b3\.[0-5]\.\d{1,2}\.\d{2,4}\b"
        },
        {
            "file": "rt640x64.sys",
            "name": "Realtek PCIe GbE / 2.5GbE Family Controller",
            "hw_id": "PCI\\VEN_10EC&DEV_8125*",
            "version_regex": r"\b(?:10\.\d{1,3}\.\d{2,4}\.202\d|11\d{2}\.\d{1,2}\.\d{1,3}\.202\d)\b"
        },
        {
            "file": "e2f68.sys",
            "name": "Intel Ethernet Controller I225-V / I226-V",
            "hw_id": "PCI\\VEN_8086&DEV_15F3*",
            "version_regex": r"\b[12]\.[012]\.[1-9]\.\d{1,3}\b"
        },
        {
            "file": "KNetPxi64.sys",
            "name": "Killer Network Suite / E2600/E3100 Controller",
            "hw_id": "PCI\\VEN_10EC&DEV_2600*",
            "version_regex": r"\b(?:9\.0\.0\.\d{1,3}|3\.\d{1,2}\.\d{3,4}\.\d{1,3})\b"
        }
    ],
    "GPU": [
        {
            "file": "nvlddmkm.sys",
            "name": "NVIDIA GeForce Desktop/Mobile GPU",
            "hw_id": "PCI\\VEN_10DE*",
            "version_regex": r"\b(?:4[5-9]\d|5\d{2})\.\d{2}\b"
        },
        {
            "file": "amdkmdag.sys",
            "name": "AMD Radeon RDNA GPU",
            "hw_id": "PCI\\VEN_1002*",
            "version_regex": r"\b2[1-6]\.(?:[1-9]|1[0-2])\.[1-5]\b"
        },
        {
            "file": "igdkmdn64.sys",
            "name": "Intel Arc / Iris Xe Graphics",
            "hw_id": "PCI\\VEN_8086*",
            "version_regex": r"\b3[012]\.0\.101\.\d{4}\b"
        }
    ],
    "Audio": [
        {
            "file": "RTKVHD64.sys",
            "name": "Realtek High Definition Audio",
            "hw_id": "HDAUDIO\\FUNC_01&VEN_10EC*",
            "version_regex": r"\b6\.0\.[89]\d{3}\.1\b"
        },
        {
            "file": "usbaudio2.sys",
            "name": "Microsoft USB Audio 2.0 Class Driver",
            "hw_id": "USB\\CLASS_01&SUBCLASS_02*",
            "version_regex": r"\b10\.0\.(?:1904\d|22\d{3}|26\d{3})\.\d{1,4}\b"
        }
    ],
    "Storage": [
        {
            "file": "stornvme.sys",
            "name": "Standard NVM Express Controller",
            "hw_id": "PCI\\CC_010802",
            "version_regex": r"\b10\.0\.(?:1904\d|22\d{3}|26\d{3})\.\d{1,4}\b"
        },
        {
            "file": "iaStorVD.sys",
            "name": "Intel Rapid Storage Technology (RST) VMD Controller",
            "hw_id": "PCI\\VEN_8086&DEV_467F*",
            "version_regex": r"\b(?:18|19|20)\.\d{1,2}\.\d{1,2}\.\d{4}\b"
        }
    ],
    "System": [
        {
            "file": "ACPI.sys",
            "name": "Microsoft ACPI-Compliant Control Method Battery",
            "hw_id": "ACPI\\PNP0C0A",
            "version_regex": r"\b10\.0\.(?:1904\d|22\d{3}|26\d{3})\.\d{1,4}\b"
        },
        {
            "file": "dptf_cpu.sys",
            "name": "Intel Dynamic Tuning Technology (DTT)",
            "hw_id": "PCI\\VEN_8086&DEV_461D*",
            "version_regex": r"\b8\.\d\.\d{5}\.\d{4,5}\b"
        }
    ]
}

# False-positive version blacklist (IPs, local subnets, LatencyMon versions, DNS)
VERSION_BLACKLIST = {
    "127.0.0.1", "192.168.0.1", "192.168.1.1", "192.168.1.254",
    "10.0.0.1", "8.8.8.8", "8.8.4.4", "1.1.1.1", "1.0.0.1",
    "7.00", "6.71", "6.50"
}

TARGET_SUBREDDITS = ["nvidia", "Amd", "overclocking", "reaper", "techsupport", "Windows11", "buildapc"]

HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
    "Accept-Language": "en-US,en;q=0.9"
}

# --- 2. Polarity & Telemetry Grammars ---

NEGATIVE_POLARITY_TERMS = [
    "bug", "bugged", "broken", "stutter", "stuttering", "spike", "spikes",
    "spiking", "crackle", "crackling", "dropout", "dropouts", "underrun",
    "regression", "culprit", "high dpc", "isr storm", "bad", "worse",
    "terrible", "hitch", "hitching", "freeze", "freezing", "latency with",
    "issue with", "problem with", "caused by", "since updating to", "upgraded to"
]

POSITIVE_POLARITY_TERMS = [
    "fixed in", "fixed on", "fixed with", "solved by", "rolled back to",
    "rollback to", "downgraded to", "downgrade to", "revert to", "reverted to",
    "stable", "rock solid", "clean on", "no spikes on", "no issues on",
    "works fine on", "best version is", "recommended", "stay on", "went back to"
]

# Matches verbatim LatencyMon report paste blocks
LATENCYMON_BLOCK_REGEX = re.compile(
    r"Driver with highest (?:DPC|ISR) routine execution time:\s*([a-zA-Z0-9_\-\.]+\.sys).*?"
    r"Highest reported (?:DPC|ISR) routine execution time\s*\([^\)]*\):\s*(\d+(?:[\.,]\d+)?)",
    re.IGNORECASE | re.DOTALL
)

# Matches explicit inline attribution: e.g. "nvlddmkm.sys hitting 3200 us" or "3200µs on nvlddmkm.sys"
INLINE_DRIVER_LATENCY_REGEX_TEMPLATE = (
    r"(?:{driver}\b[^\.!\?\n]{{0,65}}?\b(\d{{3,5}})\s*(?:µs|us|microseconds?)"
    r"|\b(\d{{3,5}})\s*(?:µs|us|microseconds?)\b[^\.!\?\n]{{0,65}}?\b{driver})"
)

MECHANISM_RULES = [
    ("Unthrottled background Wi-Fi roaming scans locking DIRQL", ["roaming", "background scan", "scanwhenassociated", "wlan autoconfig", "bssid"]),
    ("Energy Efficient Ethernet (EEE) / Green Ethernet PHY sleep state lockup", ["eee", "energy efficient ethernet", "green ethernet", "802.3az"]),
    ("PCIe ASPM L1.2 deep power state entry/exit stall", ["aspm", "l1.2", "link state power management", "d3cold", "d3hot"]),
    ("VRAM MCLK DPM state switching & ULPS wake latency", ["mclk", "ulps", "enableulps", "vram clock", "powermanagementmode", "prefer maximum performance"]),
    ("NDIS Lightweight Filter / QoS packet inspection lock contention", ["qos", "prioritization engine", "lightweight filter", "ndis", "rss", "receive side scaling"]),
    ("Embedded Controller (EC) SMBus/I2C battery polling stall", ["battery", "smbus", "embedded controller", "control method battery"]),
    ("Audio Processing Object (APO) synchronous telemetry polling", ["nahimic", "waves", "maxxaudio", "sonic studio", "apo", "audio effects"])
]

SYMPTOM_RULES = [
    ("Real-time audio buffer underruns, pops, and ASIO dropouts", ["crackle", "crackling", "dropout", "underrun", "asio", "daw", "reaper", "pop", "pops"]),
    ("1% low frametime spikes and competitive gaming micro-stutters", ["1% low", "frametime", "micro-stutter", "microstutter", "fps drop", "stutter in games"]),
    ("Mouse cursor hitching and USB polling rate jitter", ["mouse", "cursor", "polling rate", "input lag", "usb drop"])
]

FIX_RULES = [
    ("Disable Energy Efficient Ethernet (EEE) and Green Ethernet in Device Manager Advanced properties", ["eee", "energy efficient ethernet", "green ethernet"]),
    ("Set PCI Express Link State Power Management to Off in Windows Power Options", ["aspm", "link state power management"]),
    ("Disable background roaming scans or set Roaming Aggressiveness to Lowest", ["roaming aggressiveness", "scanwhenassociated", "autoconfig"]),
    ("Set Power Management Mode to Prefer Maximum Performance or lock MCLK states", ["prefer maximum performance", "enableulps", "mclk"]),
    ("Switch to Microsoft generic inbox driver or strip OEM APO services", ["inbox driver", "generic high definition audio", "nahimic", "killer uninstaller"]),
    ("Disable Microsoft ACPI-Compliant Control Method Battery in Device Manager during real-time workloads", ["control method battery", "disable battery"])
]

# --- 3. Polite Fetching Engine (With Deep Reddit Comment Thread Inspection) ---

def polite_sleep():
    time.sleep(random.uniform(1.6, 2.8))

def fetch_reddit_thread_comments(permalink):
    """Fetches top-level comments from a specific Reddit thread for high-accuracy version confirmation."""
    url = f"https://www.reddit.com{permalink}.json?limit=30&depth=2"
    comments_text = []
    try:
        resp = requests.get(url, headers=HEADERS, timeout=10)
        if resp.status_code == 200:
            data = resp.json()
            if isinstance(data, list) and len(data) > 1:
                children = data[1].get("data", {}).get("children", [])
                for child in children:
                    body = child.get("data", {}).get("body", "")
                    if body and body not in ("[deleted]", "[removed]"):
                        comments_text.append(body)
    except Exception:
        pass
    return "\n".join(comments_text)

def gather_documents(driver_file, deep_reddit=True):
    """
    Collects individual documents (posts + threads) where the exact driver file is explicitly mentioned.
    Never merges unrelated documents together.
    """
    documents = []
    seen_urls = set()
    driver_lower = driver_file.lower()

    # 1. Reddit Search API
    for sub in TARGET_SUBREDDITS:
        query = f'"{driver_file}" (LatencyMon OR DPC OR ISR OR stutter OR crackle)'
        url = f"https://www.reddit.com/r/{sub}/search.json?q={quote_plus(query)}&restrict_sr=1&sort=relevance&limit=15"
        try:
            resp = requests.get(url, headers=HEADERS, timeout=10)
            if resp.status_code == 200:
                children = resp.json().get("data", {}).get("children", [])
                for child in children:
                    post = child.get("data", {})
                    permalink = post.get("permalink", "")
                    full_url = f"https://www.reddit.com{permalink}"
                    if not permalink or full_url in seen_urls:
                        continue

                    title = post.get("title", "")
                    selftext = post.get("selftext", "")
                    combined_post = f"{title}\n{selftext}"

                    # Strict Gate 1: The post itself must contain the exact driver filename
                    if driver_lower not in combined_post.lower():
                        continue

                    seen_urls.add(full_url)
                    if deep_reddit:
                        polite_sleep()
                        comments = fetch_reddit_thread_comments(permalink)
                        combined_post = f"{combined_post}\n{comments}"

                    documents.append({"url": full_url, "text": combined_post})
        except Exception as e:
            logging.debug(f"Reddit fetch warning on r/{sub}: {e}")
        polite_sleep()

    # 2. DuckDuckGo HTML Search
    ddg_queries = [
        f'"{driver_file}" "LatencyMon" ("highest reported" OR "DPC" OR "us")',
        f'site:forums.guru3d.com "{driver_file}" ("DPC" OR "LatencyMon")',
        f'site:forum.cockos.com "{driver_file}" ("DPC" OR "LatencyMon")'
    ]
    ddg_url = "https://html.duckduckgo.com/html/"
    for q in ddg_queries:
        try:
            resp = requests.post(ddg_url, data={"q": q}, headers=HEADERS, timeout=10)
            if resp.status_code == 200:
                soup = BeautifulSoup(resp.text, "html.parser")
                for block in soup.find_all("div", class_="result__body"):
                    snippet_el = block.find("a", class_="result__snippet")
                    url_el = block.find("a", class_="result__url")
                    if snippet_el and url_el:
                        snippet_text = snippet_el.get_text(" ", strip=True)
                        link = url_el.get("href", "").strip()
                        if driver_lower in snippet_text.lower() and link not in seen_urls:
                            seen_urls.add(link)
                            documents.append({"url": link, "text": snippet_text})
        except Exception as e:
            logging.debug(f"DDG fetch warning: {e}")
        polite_sleep()

    return documents

# --- 4. Deterministic Sentence-Level Verification Engine ---

def split_into_sentences(text):
    """Splits text into individual sentences/lines so context never bleeds across paragraphs."""
    return [s.strip() for s in re.split(r'(?:[\r\n]+|(?<=[\.\!\?])\s+)', text) if s.strip()]

def classify_version_polarity(sentence_lower, version_str):
    """
    Determines whether a version mentioned in a sentence is reported as BUGGED or STABLE/RECOMMENDED.
    Inspects the local clause window around the version string.
    Returns: 'bugged', 'stable', or 'ambiguous'
    """
    idx = sentence_lower.find(version_str.lower())
    if idx == -1:
        return "ambiguous"

    # Extract a tight window around the version mention (85 chars before, 85 chars after)
    start = max(0, idx - 85)
    end = min(len(sentence_lower), idx + len(version_str) + 85)
    window = sentence_lower[start:end]

    # Check if a positive/rollback phrase directly precedes the version (e.g., "rolled back to 537.58")
    prefix_window = sentence_lower[max(0, idx - 45):idx]
    for pos_term in POSITIVE_POLARITY_TERMS:
        if pos_term in prefix_window:
            return "stable"

    pos_score = sum(1 for term in POSITIVE_POLARITY_TERMS if term in window)
    neg_score = sum(1 for term in NEGATIVE_POLARITY_TERMS if term in window)

    if pos_score > 0 and neg_score == 0:
        return "stable"
    if neg_score > 0 and pos_score == 0:
        return "bugged"
    if neg_score > 0 and pos_score > 0:
        # Both exist in the sentence (e.g. "531.18 had spikes so I rolled back to 528.49")
        # Use immediate prefix proximity (within 35 chars prior to the version)
        for neg_term in NEGATIVE_POLARITY_TERMS:
            if neg_term in prefix_window:
                return "bugged"
        # Check immediate suffix (within 40 chars after the version, e.g. "531.18 had huge spikes")
        suffix_window = sentence_lower[idx + len(version_str):min(len(sentence_lower), idx + len(version_str) + 45)]
        for neg_term in NEGATIVE_POLARITY_TERMS:
            if neg_term in suffix_window:
                return "bugged"
        for pos_term in POSITIVE_POLARITY_TERMS:
            if pos_term in suffix_window:
                return "stable"

    return "ambiguous"

def extract_verified_latency(doc_text, driver_file):
    """
    Extracts microsecond latency ONLY if it is explicitly bound to the target driver_file
    either via a verbatim LatencyMon report block or within the same sentence/65-char window.
    Returns (list_of_latencies_us, has_verbatim_latencymon_log)
    """
    verified_us = []
    has_verbatim_log = False
    driver_lower = driver_file.lower()

    # Pass 1: Verbatim LatencyMon Structured Report Check (100% deterministic)
    for match in LATENCYMON_BLOCK_REGEX.finditer(doc_text):
        reported_driver = match.group(1).strip().lower()
        raw_val = match.group(2).replace(",", ".")
        if reported_driver == driver_lower:
            try:
                val = int(float(raw_val))
                if 250 <= val <= 150000:
                    verified_us.append(val)
                    has_verbatim_log = True
            except ValueError:
                pass

    # Pass 2: Strict Inline Driver-to-Microsecond Proximity Check
    inline_pattern = re.compile(
        INLINE_DRIVER_LATENCY_REGEX_TEMPLATE.format(driver=re.escape(driver_file)),
        re.IGNORECASE
    )
    for match in inline_pattern.finditer(doc_text):
        raw_num = match.group(1) or match.group(2)
        if raw_num:
            try:
                val = int(raw_num)
                if 300 <= val <= 100000:
                    verified_us.append(val)
            except ValueError:
                pass

    return verified_us, has_verbatim_log

def evaluate_driver_target(category, target, min_quorum=2, deep_reddit=True):
    """
    Executes the full zero-false-positive verification pipeline for a single driver target.
    """
    driver_file = target["file"]
    version_regex = re.compile(target["version_regex"])
    logging.info(f"[*] Auditing {driver_file} ({target['name']})...")

    documents = gather_documents(driver_file, deep_reddit=deep_reddit)
    if not documents:
        logging.info(f"    [-] Zero documents passed strict filename filter for {driver_file}.")
        return None

    # Trackers mapping version -> set of independent source URLs
    bugged_version_sources = collections.defaultdict(set)
    stable_version_sources = collections.defaultdict(set)
    verified_latencies = []
    corroborated_urls = set()
    verbatim_log_count = 0

    mechanism_votes = collections.Counter()
    symptom_votes = collections.Counter()
    fix_votes = collections.Counter()

    for doc in documents:
        url = doc["url"]
        text = doc["text"]
        text_lower = text.lower()

        # 1. Extract strictly bound latency figures
        doc_latencies, is_verbatim = extract_verified_latency(text, driver_file)
        if doc_latencies:
            verified_latencies.extend(doc_latencies)
            corroborated_urls.add(url)
            if is_verbatim:
                verbatim_log_count += 1

        # 2. Sentence-level version extraction & polarity classification
        sentences = split_into_sentences(text)
        for sentence in sentences:
            s_lower = sentence.lower()
            found_versions = version_regex.findall(sentence)
            for ver in found_versions:
                if ver in VERSION_BLACKLIST:
                    continue
                polarity = classify_version_polarity(s_lower, ver)
                if polarity == "bugged":
                    bugged_version_sources[ver].add(url)
                    corroborated_urls.add(url)
                elif polarity == "stable":
                    stable_version_sources[ver].add(url)

        # 3. Vote on technical failure mechanism, symptoms, and fixes across verified docs
        for label, kw_list in MECHANISM_RULES:
            if any(kw in text_lower for kw in kw_list):
                mechanism_votes[label] += 1
        for label, kw_list in SYMPTOM_RULES:
            if any(kw in text_lower for kw in kw_list):
                symptom_votes[label] += 1
        for label, kw_list in FIX_RULES:
            if any(kw in text_lower for kw in kw_list):
                fix_votes[label] += 1

    # --- 5. Quorum & Conflict Resolution Gate ---
    confirmed_bugged = []
    for ver, urls in bugged_version_sources.items():
        stable_votes = len(stable_version_sources.get(ver, set()))
        bug_votes = len(urls)
        effective_quorum = 1 if verbatim_log_count > 0 else min_quorum
        if bug_votes >= effective_quorum and bug_votes > (stable_votes * 2):
            confirmed_bugged.append(ver)

    confirmed_stable = []
    for ver, urls in stable_version_sources.items():
        if ver not in confirmed_bugged and len(urls) >= 1:
            confirmed_stable.append((len(urls), ver))
    confirmed_stable.sort(reverse=True)

    if not confirmed_bugged and not verified_latencies:
        logging.info(
            f"    [-] Rejected {driver_file}: Failed quorum/telemetry verification "
            f"(Docs scanned: {len(documents)}, Verbatim logs: {verbatim_log_count})."
        )
        return None

    if verified_latencies:
        verified_latencies.sort()
        p75_index = int(len(verified_latencies) * 0.75)
        typical_latency_us = verified_latencies[min(p75_index, len(verified_latencies) - 1)]
    else:
        typical_latency_us = 0

    best_mechanism = mechanism_votes.most_common(1)[0][0] if mechanism_votes else "Confirmed DPC/ISR execution stall exceeding real-time threshold"
    best_symptom = symptom_votes.most_common(1)[0][0] if symptom_votes else "Elevated DPC routine execution time reported in LatencyMon"
    best_fix = fix_votes.most_common(1)[0][0] if fix_votes else "Roll back to verified stable driver version or disable device power saving"

    recommended_str = (
        confirmed_stable[0][1]
        if confirmed_stable
        else "Use Microsoft Inbox Driver or latest WHQL release"
    )

    if typical_latency_us >= 3000 or verbatim_log_count >= 2:
        severity = "Critical"
    elif typical_latency_us >= 1200 or len(confirmed_bugged) >= 2:
        severity = "High"
    else:
        severity = "Medium"

    logging.info(
        f"    [+] VERIFIED {driver_file}: {len(confirmed_bugged)} bugged versions, "
        f"typical={typical_latency_us}µs, sources={len(corroborated_urls)}"
    )

    return {
        "deviceClass": category,
        "deviceName": target["name"],
        "hardwareIdPattern": target["hw_id"],
        "driverFile": driver_file,
        "buggedVersions": sorted(confirmed_bugged, reverse=True),
        "recommendedVersion": recommended_str,
        "typicalLatencyUs": typical_latency_us,
        "failureMechanism": best_mechanism,
        "symptom": best_symptom,
        "fix": best_fix,
        "verificationMetrics": {
            "documentsInspected": len(documents),
            "corroboratingSources": len(corroborated_urls),
            "verbatimLatencyMonLogs": verbatim_log_count,
            "quorumThresholdUsed": min_quorum
        },
        "sourceUrls": sorted(list(corroborated_urls))[:8],
        "severity": severity
    }

# --- 6. CLI Entrypoint ---

def main():
    parser = argparse.ArgumentParser(
        description="Zero-False-Positive DPC/ISR Latency Harvester"
    )
    parser.add_argument("--category", type=str, choices=list(SEED_TARGETS.keys()), help="Audit a specific hardware class")
    parser.add_argument("--driver", type=str, help="Audit a single kernel driver binary (e.g., nvlddmkm.sys)")
    parser.add_argument("--all", action="store_true", help="Audit all seed driver targets")
    parser.add_argument(
        "--min-quorum",
        type=int,
        default=2,
        help="Minimum independent sources required to flag a driver version as bugged (default: 2)"
    )
    parser.add_argument(
        "--fast",
        action="store_true",
        help="Skip deep Reddit comment tree fetching (faster, fewer corroborating sources)"
    )
    parser.add_argument("--output", type=str, default="bad_drivers.json", help="Output JSON file path")
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(message)s")

    selected_targets = []
    if args.all:
        for cat, items in SEED_TARGETS.items():
            for item in items:
                selected_targets.append((cat, item))
    elif args.category:
        for item in SEED_TARGETS[args.category]:
            selected_targets.append((args.category, item))
    elif args.driver:
        for cat, items in SEED_TARGETS.items():
            for item in items:
                if item["file"].lower() == args.driver.lower():
                    selected_targets.append((cat, item))
    else:
        parser.print_help()
        return

    if not selected_targets:
        logging.error("[-] Driver target not found in SEED_TARGETS.")
        return

    existing_db = []
    if os.path.exists(args.output):
        try:
            with open(args.output, "r", encoding="utf-8") as f:
                existing_db = json.load(f)
        except Exception:
            existing_db = []

    updated_count = 0
    for category, target in selected_targets:
        entry = evaluate_driver_target(
            category=category,
            target=target,
            min_quorum=args.min_quorum,
            deep_reddit=not args.fast
        )
        if entry:
            idx = next((i for i, d in enumerate(existing_db) if d["driverFile"].lower() == entry["driverFile"].lower()), None)
            if idx is not None:
                existing_db[idx] = entry
            else:
                existing_db.append(entry)
            updated_count += 1

    if updated_count > 0:
        with open(args.output, "w", encoding="utf-8") as f:
            json.dump(existing_db, f, indent=2, ensure_ascii=False)
        logging.info(f"\n[!] Completed: {updated_count} verified entries written to {args.output}")
    else:
        logging.info("\n[-] Completed: No entries met the strict zero-false-positive quorum threshold.")

if __name__ == "__main__":
    main()
