#!/bin/sh
# Joins an English list and its German translation (same line order) into a
# table for the plugin: build/tr/<name>.en + build/tr/<name>.de -> src/GedoniaCoop/lang/de-<nn>-<name>.tsv
# Refuses when line counts, line breaks or placeholders differ.
set -e
name=$1; nn=$2
en=build/tr/$name.en; de=build/tr/$name.de
[ "$(wc -l < $en)" = "$(wc -l < $de)" ] || { echo "line counts differ: $(wc -l < $en) vs $(wc -l < $de)"; exit 1; }
paste $en $de | awk -F'\t' '{
  e=$1; d=$2; ne=gsub(/\n/,"",e); nd=gsub(/\n/,"",d);
  pe=$1; pd=$2; se=gsub(/\{[0-9]+\}/,"",pe); sd=gsub(/\{[0-9]+\}/,"",pd);
  if (se!=sd) { print "placeholders differ on line " NR ": " $1 > "/dev/stderr"; bad=1 }
  if (ne!=nd) print "note: line breaks differ on line " NR > "/dev/stderr"
} END { exit bad }'
{ echo "# $name: English<TAB>German"; paste $en $de | awk -F'\t' '$2 != "" && $1 != $2'; } > src/GedoniaCoop/lang/de-$nn-$name.tsv
echo "$(grep -vc '^#' src/GedoniaCoop/lang/de-$nn-$name.tsv) entries -> src/GedoniaCoop/lang/de-$nn-$name.tsv"
