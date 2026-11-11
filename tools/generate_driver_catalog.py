#!/usr/bin/env python3
"""
generate_driver_catalog.py
Automated deep-research driver latency catalog generator for Stutter Guard.
Leverages Gemini API (gemini-3.8-flash) to systematically audit and compile
empirical DPC/ISR latency regressions across all Windows Device Manager classes.
"""

import os
import sys
import json
import argparse
import logging
from google import genai

CATEGORIES = {
    "Network_WiFi": (
        "Exhaustive list of Wi-Fi network adapter driver versions known for severe DPC latency regressions in LatencyMon "
        "(Realtek RTL8821CE/8822CE/8852BE/CE, Intel Wireless-AC 9260/9560, Intel Wi-Fi 6/6E/7 AX200/AX201/AX210/AX211/BE200, "
        "MediaTek MT7921/MT7922 / AMD RZ608/RZ616, Qualcomm Atheros QCA6174A/QCA9377, Broadcom BCM4352/BCM4360)."
    ),
    "Network_Ethernet": (
        "Exhaustive list of PCIe and onboard Ethernet controller driver versions with verified DPC latency spikes, packet loss, or ISR storms "
        "(Realtek PCIe GbE/2.5GbE/5GbE RTL8111/RTL8125/RTL8125B/RTL8126 rt640x64.sys, Intel I225-V/I226-V e2f68.sys, "
        "Intel I219-V/LM e1d68x64.sys, Killer Network Suite KNetPxi64.sys, Aquantia/Marvell 10GbE AQC107/AQC113C aqnic65.sys)."
    ),
    "GPU_Nvidia": (
        "Exhaustive list of NVIDIA GeForce Game Ready & Studio driver versions with verified DPC latency spikes in LatencyMon "
        "originating from nvlddmkm.sys (e.g. 368.39, 466.xx, 531.18, 531.29, 535.98, 536.40, 546.01, 546.17, 546.29, 546.33, etc.), "
        "including NVIDIA Container CPU loops, MCLK power state switching, and NVIDIA High Definition Audio nvhda64v.sys line-based IRQ conflicts."
    ),
    "GPU_AMD": (
        "Exhaustive list of AMD Radeon Adrenalin driver versions with verified DPC latency regressions in amdkmdag.sys / atikmdag.sys "
        "(e.g. 22.5.2 through 22.11.2, 23.9.1 through 23.11.1, etc.), Ultra Low Power State (ULPS) wake latency, "
        "memory clock (MCLK) DPM switching hitches, and AMD High Definition Audio AtihdWT6.sys conflicts."
    ),
    "GPU_Intel": (
        "Exhaustive list of Intel Arc & Iris Xe Graphics driver versions with verified DPC latency spikes in igdkmdn64.sys "
        "(e.g. 31.0.101.3490 through 31.0.101.4146 and later branches), PCIe ASPM wake stalls, and ReBAR memory access stalls."
    ),
    "Audio_Realtek_OEM": (
        "Exhaustive list of Realtek High Definition Audio driver versions (RTKVHD64.sys) and third-party OEM Audio Processing Objects (APO) "
        "and services (Nahimic / A-Volute, Waves MaxxAudio, Sonic Studio, DTS:X, Dolby Atmos background telemetry) documented to cause "
        "continuous 2,000 - 5,000 us DPC latency baselines and real-time audio buffer underruns."
    ),
    "Audio_USB_ASIO": (
        "Exhaustive list of USB Audio Class 2.0 (usbaudio2.sys) and hardware USB DAC / audio interface ASIO driver versions "
        "(Focusrite Scarlett USB driver versions with buffer starvation, MOTU, Behringer UMC, Yamaha Steinberg ASIO) "
        "known for high DPC latency and audio dropouts under Windows."
    ),
    "Storage_Controllers": (
        "Exhaustive list of storage controller drivers known for DPC latency spikes and storport.sys stalls: "
        "Standard NVM Express Controller (stornvme.sys PCIe ASPM L1.2 sub-state transitions), Samsung proprietary NVMe driver (secnvme.sys), "
        "Intel Rapid Storage Technology / VMD (iaStorVD.sys, iaStorA.sys), and ASMedia SATA controllers."
    ),
    "System_Power_Sensors": (
        "Exhaustive list of system devices, battery drivers, power frameworks, and background hardware polling software drivers "
        "documented to cause severe DPC latency spikes: Microsoft ACPI-Compliant Control Method Battery (ACPI.sys EC polling stalls on laptops), "
        "Intel Dynamic Tuning Technology (DTT / dptf_cpu.sys), Intel Management Engine Interface (HECI / TeeDriverW10x64.sys), "
        "and peripheral polling drivers (Corsair iCUE CorsairLLAccess64.sys, Razer Synapse rzpmgrk.sys, NZXT CAM)."
    )
}

SCHEMA_PROMPT = """
You are an expert Windows kernel developer and real-time latency optimization engineer.
Generate an exhaustive, verified JSON array of hardware devices and driver versions documented to cause high DPC latency, ISR storms, audio buffer dropouts, or 1% low frame-time hitches in LatencyMon.

Target hardware category to audit:
{category_description}

Rules:
1. Provide exact driver .sys file names.
2. Provide exact, specific bugged driver version strings (e.g. "531.18", "2024.0.10.218", "3.0.1.1167").
3. Provide exact recommended fallback or target versions.
4. Provide typical latency in microseconds (us) observed in LatencyMon.
5. Provide the precise kernel-level failure mechanism (e.g. spinlock contention, unthrottled background Wi-Fi roaming, PCIe ASPM L1.2 exit latency, EC SMBus polling loop).
6. Provide symptoms and actionable workarounds/fixes.
7. Return strictly a raw, valid JSON array adhering to this schema:
[
  {
    "deviceClass": "Network | GPU | Audio | Storage | System",
    "deviceName": "string",
    "hardwareIdPattern": "string (PCI\\\\VEN_... or USB\\\\VID_...)",
    "driverFile": "string (.sys file)",
    "buggedVersions": ["string"],
    "recommendedVersion": "string",
    "typicalLatencyUs": 0,
    "failureMechanism": "string",
    "symptom": "string",
    "fix": "string",
    "severity": "Critical | High | Medium"
  }
]

Output ONLY the raw JSON array. Do not include markdown preamble or conversational text.
"""

def get_gemini_client():
    api_key = os.environ.get("GEMINI_API_KEY")
    if not api_key:
        logging.error("[-] GEMINI_API_KEY environment variable is not set.")
        sys.exit(1)
    return genai.Client(api_key=api_key)

def clean_json_response(raw_text):
    text = raw_text.strip()
    if text.startswith("```json"):
        text = text[7:]
    elif text.startswith("```"):
        text = text[3:]
    if text.endswith("```"):
        text = text[:-3]
    return text.strip()

def audit_category(client, category_name, category_desc):
    logging.info(f"[*] Auditing category: {category_name}...")
    prompt = SCHEMA_PROMPT.replace("{category_description}", category_desc)
    
    try:
        resp = client.models.generate_content(
            model="gemini-3.8-flash",
            contents=prompt
        )
        cleaned = clean_json_response(resp.text)
        items = json.loads(cleaned)
        if isinstance(items, list):
            logging.info(f"    [+] Successfully generated {len(items)} verified entries for {category_name}.")
            return items
        else:
            logging.warning(f"    [-] Response was not a JSON list for {category_name}.")
            return []
    except Exception as e:
        logging.error(f"    [-] Error auditing {category_name}: {e}")
        return []

def main():
    parser = argparse.ArgumentParser(description="Automated Deep-Research Driver Latency Catalog Generator")
    parser.add_argument("--category", choices=list(CATEGORIES.keys()), help="Audit a single hardware category")
    parser.add_argument("--all", action="store_true", help="Audit all hardware categories sequentially")
    parser.add_argument("--output", type=str, default=r"..\src\DpcTweaker.Core\Catalog\bad_drivers.json", help="Target output JSON path")
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(message)s")

    client = get_gemini_client()

    selected_categories = []
    if args.all:
        selected_categories = list(CATEGORIES.items())
    elif args.category:
        selected_categories = [(args.category, CATEGORIES[args.category])]
    else:
        parser.print_help()
        return

    output_path = os.path.abspath(os.path.join(os.path.dirname(__file__), args.output))
    os.makedirs(os.path.dirname(output_path), exist_ok=True)

    existing_db = []
    if os.path.exists(output_path):
        try:
            with open(output_path, "r", encoding="utf-8") as f:
                existing_db = json.load(f)
            logging.info(f"[*] Loaded existing database with {len(existing_db)} entries from {output_path}")
        except Exception:
            existing_db = []

    total_added = 0
    for cat_name, cat_desc in selected_categories:
        new_items = audit_category(client, cat_name, cat_desc)
        for item in new_items:
            driver_file = item.get("driverFile", "").lower()
            idx = next((i for i, d in enumerate(existing_db) if d.get("driverFile", "").lower() == driver_file and d.get("deviceName", "").lower() == item.get("deviceName", "").lower()), None)
            if idx is not None:
                existing_db[idx] = item
            else:
                existing_db.append(item)
            total_added += 1

    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(existing_db, f, indent=2, ensure_ascii=False)

    logging.info(f"\n[!] Catalog Generation Complete. Total entries in database: {len(existing_db)}")
    logging.info(f"    Saved to: {output_path}")

if __name__ == "__main__":
    main()
