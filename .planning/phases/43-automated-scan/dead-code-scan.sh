#!/bin/bash

echo "=== Dead Code Detection Scan ==="
echo "Date: $(date)"
echo ""

# Count total .cs files
echo "## File Statistics"
TOTAL_CS_FILES=$(find . -name "*.cs" -type f ! -path "./obj/*" ! -path "./bin/*" ! -path "./.git/*" | wc -l)
echo "Total .cs files: $TOTAL_CS_FILES"
echo ""

# Find large commented code blocks (>10 consecutive lines starting with //)
echo "## Commented-Out Code Blocks (>10 consecutive lines)"
find . -name "*.cs" -type f ! -path "./obj/*" ! -path "./bin/*" -exec awk '
BEGIN { count = 0; start_line = 0; file = "" }
/^[[:space:]]*\/\// { 
    if (count == 0) start_line = NR
    count++ 
}
!/^[[:space:]]*\/\// { 
    if (count > 10) {
        if (file != FILENAME) {
            file = FILENAME
            print ""
            print "File: " FILENAME
        }
        print "  Lines " start_line "-" (NR-1) ": " count " commented lines"
    }
    count = 0 
}
END {
    if (count > 10) {
        if (file != FILENAME) print "\nFile: " FILENAME
        print "  Lines " start_line "-" NR ": " count " commented lines"
    }
}' {} \; > /tmp/dead-code-blocks.txt
COMMENT_BLOCKS=$(cat /tmp/dead-code-blocks.txt | grep -c "Lines" || echo "0")
echo "Found $COMMENT_BLOCKS large commented code blocks"
cat /tmp/dead-code-blocks.txt | head -100
echo ""

# Find commented using statements
echo "## Commented Using Statements"
COMMENTED_USINGS=$(grep -r "^[[:space:]]*//[[:space:]]*using " . --include="*.cs" --exclude-dir=obj --exclude-dir=bin | wc -l)
echo "Found $COMMENTED_USINGS commented using statements"
grep -r "^[[:space:]]*//[[:space:]]*using " . --include="*.cs" --exclude-dir=obj --exclude-dir=bin | head -20
echo ""

# Find .cs files not in any .csproj
echo "## Orphaned .cs Files"
echo "Checking for .cs files not referenced in any .csproj..."
find . -name "*.cs" -type f ! -path "./obj/*" ! -path "./bin/*" > /tmp/all-cs-files.txt
find . -name "*.csproj" -exec cat {} \; | grep -o 'Include="[^"]*\.cs"' | sed 's/Include="//;s/"//' > /tmp/referenced-cs-files.txt
# This is a simplified check - full analysis would need proper XML parsing
echo "Total .cs files: $(wc -l < /tmp/all-cs-files.txt)"
echo "Note: Full orphaned file detection requires XML parsing of all .csproj files"
echo ""

# Summary
echo "## Summary"
echo "Total .cs files: $TOTAL_CS_FILES"
echo "Large commented blocks: $COMMENT_BLOCKS"
echo "Commented using statements: $COMMENTED_USINGS"
