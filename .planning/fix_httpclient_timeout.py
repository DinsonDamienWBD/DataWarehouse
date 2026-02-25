import re, os, glob

PLUGIN = 'Plugins/DataWarehouse.Plugins.UltimateIntelligence'
files = glob.glob(f'{PLUGIN}/**/*.cs', recursive=True)
modified = 0

for fpath in files:
    with open(fpath, 'r', encoding='utf-8-sig') as f:
        content = f.read()
    original = content

    # Replace: private static readonly HttpClient SharedHttpClient = new HttpClient();
    # With: private static readonly HttpClient SharedHttpClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(30) };
    content = content.replace(
        'private static readonly HttpClient SharedHttpClient = new HttpClient();',
        'private static readonly HttpClient SharedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };'
    )

    if content != original:
        with open(fpath, 'w', encoding='utf-8-sig') as f:
            f.write(content)
        print(f'Fixed: {os.path.relpath(fpath, PLUGIN)}')
        modified += 1

print(f'\nTotal: {modified} files')
