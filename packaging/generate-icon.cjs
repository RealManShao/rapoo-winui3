// Regenerate the Windows icon from the original vector: node packaging/generate-icon.cjs
// Requires sharp; an optional first argument can point to its installed module.
const fs = require('node:fs');
const path = require('node:path');
const sharp = require(process.argv[2] || 'sharp');

async function main() {
  const assets = path.resolve(__dirname, '../src/Rapoo.App/Assets');
  const logo = fs.readFileSync(path.join(assets, 'RapooLogo.svg'), 'utf8')
    .replace(/<\?xml[^?]*\?>/, '')
    .replace('<svg ', '<svg x="20" y="97" width="216" height="62" ');
  // A light tile keeps the original purple wordmark visible on both taskbar themes.
  const svg = Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256"><rect x="8" y="8" width="240" height="240" rx="44" fill="#fff"/>${logo}</svg>`);
  const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
  const images = await Promise.all(sizes.map(size => sharp(svg).resize(size, size).png().toBuffer()));
  const header = Buffer.alloc(6 + sizes.length * 16);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(sizes.length, 4);
  let offset = header.length;
  sizes.forEach((size, i) => {
    const entry = 6 + i * 16;
    header[entry] = size === 256 ? 0 : size;
    header[entry + 1] = size === 256 ? 0 : size;
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(images[i].length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += images[i].length;
  });
  fs.writeFileSync(path.join(assets, 'Rapoo.ico'), Buffer.concat([header, ...images]));
  console.log(`Generated Rapoo.ico (${sizes.join(', ')} px) from RapooLogo.svg`);
}

main().catch(error => { console.error(error); process.exitCode = 1; });
