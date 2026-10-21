// Nura Herbex: client-side table export (CSV, Excel .xlsx, PDF) with no external libraries.
// window.nuraExport.run(format, filename, title, columns, rows, meta)
//   columns: ["Order", "Total", ...]; rows: [["NH-1", "₹1,899", ...], ...]; meta: ["Period: 1 Oct to 20 Oct", ...]
(function () {
  const enc = new TextEncoder();

  function download(name, mime, bytes) {
    const blob = new Blob([bytes], { type: mime });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = name;
    document.body.appendChild(a);
    a.click();
    setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
  }

  const clean = (v) => (v === null || v === undefined ? '' : String(v)).replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/g, '');

  // ---------------------------------------------------------------- CSV
  function csv(columns, rows) {
    const q = (v) => {
      let s = clean(v);
      // Stop spreadsheet formulas from running when a cell starts with = + - @ (customer-entered text).
      if (/^[=+\-@\t\r]/.test(s) && !/^[-+]?[\d.,₹ ]+$/.test(s)) s = "'" + s;
      return /[",\r\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s;
    };
    const lines = [columns.map(q).join(',')].concat(rows.map((r) => r.map(q).join(',')));
    return enc.encode('﻿' + lines.join('\r\n') + '\r\n');
  }

  // ---------------------------------------------------------------- XLSX (zip, stored)
  const crcTable = (() => {
    const t = new Uint32Array(256);
    for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; }
    return t;
  })();
  function crc32(bytes) { let c = 0xffffffff; for (let i = 0; i < bytes.length; i++) c = crcTable[(c ^ bytes[i]) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; }

  function zip(files) {
    const parts = [], central = [];
    let offset = 0;
    const u16 = (n) => new Uint8Array([n & 255, (n >>> 8) & 255]);
    const u32 = (n) => new Uint8Array([n & 255, (n >>> 8) & 255, (n >>> 16) & 255, (n >>> 24) & 255]);
    const cat = (...a) => { const len = a.reduce((s, x) => s + x.length, 0); const out = new Uint8Array(len); let p = 0; a.forEach((x) => { out.set(x, p); p += x.length; }); return out; };
    for (const f of files) {
      const name = enc.encode(f.name), data = enc.encode(f.data), crc = crc32(data);
      const local = cat(u32(0x04034b50), u16(20), u16(0x0800), u16(0), u16(0), u16(0x21), u32(crc), u32(data.length), u32(data.length), u16(name.length), u16(0), name, data);
      central.push(cat(u32(0x02014b50), u16(20), u16(20), u16(0x0800), u16(0), u16(0), u16(0x21), u32(crc), u32(data.length), u32(data.length), u16(name.length), u16(0), u16(0), u16(0), u16(0), u32(0), u32(offset), name));
      parts.push(local); offset += local.length;
    }
    const cd = cat(...central);
    return cat(...parts, cd, u32(0x06054b50), u16(0), u16(0), u16(files.length), u16(files.length), u32(cd.length), u32(offset), u16(0));
  }

  const xml = (s) => clean(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  const colName = (i) => { let s = ''; i++; while (i > 0) { const m = (i - 1) % 26; s = String.fromCharCode(65 + m) + s; i = Math.floor((i - 1) / 26); } return s; };
  // A cell is a number when it is plain digits (with optional ₹ , . %), so Excel can add it up; anything else stays text.
  const asNumber = (s) => {
    const t = clean(s).trim();
    if (!/^[-+]?(₹|Rs\.?\s?)?\d{1,3}(,\d{2,3})*(\.\d+)?$|^[-+]?(₹|Rs\.?\s?)?\d+(\.\d+)?$/.test(t)) return null;
    if (/^0\d/.test(t.replace(/[₹,]/g, ''))) return null; // keep IDs and phone numbers with leading zeros as text
    if (t.replace(/[^\d]/g, '').length > 11) return null; // long digit strings (phones, AWBs) stay text
    const n = Number(t.replace(/[₹,]|Rs\.?\s?/g, ''));
    return Number.isFinite(n) ? n : null;
  };

  function xlsx(columns, rows, title) {
    const widths = columns.map((c, i) => Math.min(60, Math.max(String(c).length, ...rows.slice(0, 300).map((r) => clean(r[i]).length)) + 2));
    const cellXml = (v, r, c, header) => {
      const ref = colName(c) + r;
      if (header) return `<c r="${ref}" s="1" t="inlineStr"><is><t>${xml(v)}</t></is></c>`;
      const n = asNumber(v);
      return n !== null ? `<c r="${ref}" t="n"><v>${n}</v></c>` : `<c r="${ref}" t="inlineStr"><is><t xml:space="preserve">${xml(v)}</t></is></c>`;
    };
    let sheet = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">';
    sheet += `<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>`;
    sheet += '<cols>' + widths.map((w, i) => `<col min="${i + 1}" max="${i + 1}" width="${w}" customWidth="1"/>`).join('') + '</cols><sheetData>';
    sheet += '<row r="1">' + columns.map((c, i) => cellXml(c, 1, i, true)).join('') + '</row>';
    rows.forEach((r, ri) => { sheet += `<row r="${ri + 2}">` + columns.map((_, ci) => cellXml(r[ci], ri + 2, ci, false)).join('') + '</row>'; });
    sheet += `</sheetData><autoFilter ref="A1:${colName(columns.length - 1)}${Math.max(1, rows.length + 1)}"/></worksheet>`;
    const sheetName = xml((title || 'Report').replace(/[\\/?*\[\]:]/g, ' ').slice(0, 31) || 'Report');
    return zip([
      { name: '[Content_Types].xml', data: '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>' },
      { name: '_rels/.rels', data: '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>' },
      { name: 'xl/workbook.xml', data: `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="${sheetName}" sheetId="1" r:id="rId1"/></sheets></workbook>` },
      { name: 'xl/_rels/workbook.xml.rels', data: '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>' },
      { name: 'xl/styles.xml', data: '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF235B40"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/></cellXfs></styleSheet>' },
      { name: 'xl/worksheets/sheet1.xml', data: sheet },
    ]);
  }

  // ---------------------------------------------------------------- PDF (Helvetica, landscape A4)
  // Helvetica glyph widths (per 1000 em) for ASCII 32..126.
  const HW = [278,278,355,556,556,889,667,191,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,278,278,584,584,584,556,1015,667,667,722,722,667,611,778,722,278,500,667,556,833,722,778,667,778,722,667,611,722,667,944,667,667,611,278,278,278,469,556,333,556,556,500,556,556,278,556,556,222,222,500,222,833,556,556,556,556,333,500,278,556,500,722,500,500,500,334,260,334,584];
  const HWB = [278,333,474,556,556,889,722,238,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,333,333,584,584,584,611,975,722,722,722,722,667,611,778,722,278,556,722,611,833,722,778,667,778,722,667,611,722,667,944,667,667,611,333,278,333,584,556,333,556,611,556,611,556,333,611,611,278,278,556,278,889,611,611,611,611,389,556,333,611,556,778,556,556,500,389,280,389,584];
  const pdfText = (s) => clean(s).replace(/₹/g, 'Rs.').replace(/[–—]/g, '-').replace(/[‘’]/g, "'").replace(/[“”]/g, '"').replace(/…/g, '...').replace(/[•·]/g, '-').replace(/\s+/g, ' ')
    .replace(/[^\x20-\x7e]/g, '?');
  const tw = (s, size, bold) => { const t = bold ? HWB : HW; let w = 0; for (let i = 0; i < s.length; i++) w += t[s.charCodeAt(i) - 32] || 556; return (w * size) / 1000; };
  const esc = (s) => s.replace(/\\/g, '\\\\').replace(/\(/g, '\\(').replace(/\)/g, '\\)');
  function fit(s, width, size, bold) {
    if (tw(s, size, bold) <= width) return s;
    let lo = 0, hi = s.length;
    while (lo < hi) { const mid = Math.ceil((lo + hi) / 2); if (tw(s.slice(0, mid) + '...', size, bold) <= width) lo = mid; else hi = mid - 1; }
    return s.slice(0, lo) + '...';
  }

  function pdf(title, columns, rows, meta) {
    const W = 842, H = 595, M = 28, fs = 8, hfs = 8, rowH = 15, headH = 18;
    const usable = W - M * 2;
    const cols = columns.map(pdfText);
    const data = rows.map((r) => columns.map((_, i) => pdfText(r[i])));
    // Column widths follow content length, bounded so no column is tiny or huge.
    const want = cols.map((c, i) => Math.min(190, Math.max(tw(c, hfs, true) + 12, ...data.slice(0, 200).map((r) => tw(r[i], fs, false) + 12))));
    const total = want.reduce((a, b) => a + b, 0);
    const scale = usable / total;
    const widths = want.map((w) => w * scale);
    const perPage = Math.floor((H - M * 2 - 56 - headH) / rowH);
    const pages = Math.max(1, Math.ceil(data.length / perPage));
    const generated = new Date().toLocaleString('en-IN', { dateStyle: 'medium', timeStyle: 'short' });
    const objs = [];
    const add = (s) => { objs.push(s); return objs.length; };
    add('<< /Type /Catalog /Pages 2 0 R >>');
    add('PLACEHOLDER_PAGES');
    add('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>');
    add('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>');
    const pageIds = [];
    for (let p = 0; p < pages; p++) {
      let c = '';
      const y0 = H - M;
      c += `BT /F2 15 Tf ${M} ${y0 - 10} Td 0.137 0.357 0.251 rg (${esc(pdfText(title))}) Tj ET\n`;
      let metaY = y0 - 26;
      (meta || []).slice(0, 3).forEach((m) => { c += `BT /F1 8 Tf ${M} ${metaY} Td 0.42 0.47 0.43 rg (${esc(pdfText(m))}) Tj ET\n`; metaY -= 11; });
      c += `BT /F1 8 Tf ${W - M - 190} ${y0 - 10} Td 0.42 0.47 0.43 rg (${esc('Generated ' + pdfText(generated))}) Tj ET\n`;
      let y = H - M - 56;
      c += `0.137 0.357 0.251 rg ${M} ${y - headH + 4} ${usable} ${headH} re f\n`;
      let x = M;
      cols.forEach((h, i) => { c += `BT /F2 ${hfs} Tf ${x + 5} ${y - 8} Td 1 1 1 rg (${esc(fit(h, widths[i] - 10, hfs, true))}) Tj ET\n`; x += widths[i]; });
      y -= headH;
      const slice = data.slice(p * perPage, (p + 1) * perPage);
      slice.forEach((r, ri) => {
        if (ri % 2 === 1) c += `0.953 0.965 0.949 rg ${M} ${y - rowH + 4} ${usable} ${rowH} re f\n`;
        x = M;
        r.forEach((v, i) => { c += `BT /F1 ${fs} Tf ${x + 5} ${y - 7} Td 0.12 0.2 0.16 rg (${esc(fit(v, widths[i] - 10, fs, false))}) Tj ET\n`; x += widths[i]; });
        y -= rowH;
      });
      if (!slice.length) c += `BT /F1 10 Tf ${M} ${y - 14} Td 0.42 0.47 0.43 rg (No records in this report.) Tj ET\n`;
      c += `BT /F1 8 Tf ${M} ${M - 12} Td 0.42 0.47 0.43 rg (${esc(`Nura Herbex  |  ${data.length} record${data.length === 1 ? '' : 's'}`)}) Tj ET\n`;
      c += `BT /F1 8 Tf ${W - M - 60} ${M - 12} Td 0.42 0.47 0.43 rg (${esc(`Page ${p + 1} of ${pages}`)}) Tj ET\n`;
      const contentId = add(`<< /Length ${c.length} >>\nstream\n${c}endstream`);
      const pageId = add(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${W} ${H}] /Contents ${contentId} 0 R /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> >>`);
      pageIds.push(pageId);
    }
    objs[1] = `<< /Type /Pages /Kids [${pageIds.map((i) => i + ' 0 R').join(' ')}] /Count ${pageIds.length} >>`;
    let out = '%PDF-1.4\n';
    const offsets = [];
    objs.forEach((o, i) => { offsets.push(out.length); out += `${i + 1} 0 obj\n${o}\nendobj\n`; });
    const xref = out.length;
    out += `xref\n0 ${objs.length + 1}\n0000000000 65535 f \n` + offsets.map((o) => String(o).padStart(10, '0') + ' 00000 n \n').join('');
    out += `trailer\n<< /Size ${objs.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF`;
    // Everything above is Latin-1 safe, so one byte per character.
    const bytes = new Uint8Array(out.length);
    for (let i = 0; i < out.length; i++) bytes[i] = out.charCodeAt(i) & 255;
    return bytes;
  }

  window.nuraExport = {
    run(format, filename, title, columns, rows, meta) {
      const base = (filename || 'report').replace(/[^\w\-]+/g, '_');
      const stamp = new Date().toISOString().slice(0, 10);
      if (format === 'csv') download(`${base}_${stamp}.csv`, 'text/csv;charset=utf-8', csv(columns, rows));
      else if (format === 'xlsx') download(`${base}_${stamp}.xlsx`, 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', xlsx(columns, rows, title));
      else if (format === 'pdf') download(`${base}_${stamp}.pdf`, 'application/pdf', pdf(title || 'Report', columns, rows, meta));
      return rows.length;
    },
  };
})();
