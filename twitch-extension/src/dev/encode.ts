// Test and dev helper: builds an envelope the way the site does (gzip + base64 of the state JSON). Not in production.

export async function gzipBase64(text: string): Promise<string> {
  const stream = new Blob([text]).stream().pipeThrough(new CompressionStream('gzip'));
  const bytes = new Uint8Array(await new Response(stream).arrayBuffer());
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

export async function makeEnvelope(seq: number, state: unknown, at = 1762500000000 + seq * 1000): Promise<string> {
  return JSON.stringify({ v: 1, seq, at, z: await gzipBase64(JSON.stringify(state)) });
}
