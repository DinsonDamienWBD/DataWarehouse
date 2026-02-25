import re, sys

warn_file = sys.argv[1] if len(sys.argv) > 1 else r'C:\Users\ddamien\AppData\Local\Temp\1\dw-warnings.txt'
with open(warn_file) as f:
    lines = f.readlines()

projects = {}
codes = {}
proj_codes = {}

for line in lines:
    line = line.replace('\\', '/')
    m = re.search(r'(Plugins/DataWarehouse\.Plugins\.[^/]+|DataWarehouse\.(SDK|Kernel|Shared|Tests))', line)
    proj = m.group(1) if m else 'other'
    projects[proj] = projects.get(proj, 0) + 1

    cm = re.search(r'warning (CS\d+|CA\d+)', line)
    if cm:
        code = cm.group(1)
        codes[code] = codes.get(code, 0) + 1
        key = f"{proj}|{code}"
        proj_codes[key] = proj_codes.get(key, 0) + 1

print("=== PER PROJECT ===")
for k, v in sorted(projects.items(), key=lambda x: -x[1]):
    print(f"{v:>5}  {k}")

print(f"\n=== PER CODE (total: {sum(codes.values())}) ===")
for k, v in sorted(codes.items(), key=lambda x: -x[1]):
    print(f"{v:>5}  {k}")

print("\n=== PER PROJECT+CODE (top 50) ===")
for k, v in sorted(proj_codes.items(), key=lambda x: -x[1])[:50]:
    proj, code = k.split('|')
    print(f"{v:>5}  {proj:60s} {code}")
