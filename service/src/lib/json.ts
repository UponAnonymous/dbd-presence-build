// Strict JSON reader (RFC 8259) used for signed documents and every request body.
// Differences from JSON.parse, matching the C# verifier's JsonDocumentOptions: a nesting-depth cap,
// duplicate member names rejected, and the exact source span of each value kept (for the raw payload text).

export type JsonNode =
  | { kind: "object"; entries: Map<string, JsonNode>; start: number; end: number }
  | { kind: "array"; items: JsonNode[]; start: number; end: number }
  | { kind: "string"; value: string; start: number; end: number }
  | { kind: "number"; raw: string; start: number; end: number }
  | { kind: "boolean"; value: boolean; start: number; end: number }
  | { kind: "null"; start: number; end: number };

/** Thrown for any syntax error, a depth above the cap or a duplicate member name. */
export class JsonSyntaxError extends Error {
  constructor(reason: string) {
    super(reason);
    this.name = "JsonSyntaxError";
  }
}

const NUMBER = /-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/y;

class Reader {
  private pos = 0;
  private readonly text: string;
  private readonly maxDepth: number;

  constructor(text: string, maxDepth: number) {
    this.text = text;
    this.maxDepth = maxDepth;
  }

  parseDocument(): JsonNode {
    this.skipWhitespace();
    const value = this.parseValue(0);
    this.skipWhitespace();
    if (this.pos !== this.text.length) throw new JsonSyntaxError("trailing data");
    return value;
  }

  private skipWhitespace(): void {
    const t = this.text;
    while (this.pos < t.length) {
      const c = t.charCodeAt(this.pos);
      if (c === 0x20 || c === 0x09 || c === 0x0a || c === 0x0d) this.pos++;
      else break;
    }
  }

  private parseValue(depth: number): JsonNode {
    const c = this.text[this.pos];
    switch (c) {
      case "{":
        return this.parseObject(depth + 1);
      case "[":
        return this.parseArray(depth + 1);
      case '"': {
        const start = this.pos;
        const value = this.parseString();
        return { kind: "string", value, start, end: this.pos };
      }
      case "t":
        return this.literal("true", { kind: "boolean", value: true, start: this.pos, end: this.pos + 4 });
      case "f":
        return this.literal("false", { kind: "boolean", value: false, start: this.pos, end: this.pos + 5 });
      case "n":
        return this.literal("null", { kind: "null", start: this.pos, end: this.pos + 4 });
      default:
        return this.parseNumber();
    }
  }

  private literal(word: string, node: JsonNode): JsonNode {
    if (!this.text.startsWith(word, this.pos)) throw new JsonSyntaxError("unexpected token");
    this.pos += word.length;
    return node;
  }

  private parseNumber(): JsonNode {
    NUMBER.lastIndex = this.pos;
    const match = NUMBER.exec(this.text);
    if (!match) throw new JsonSyntaxError("unexpected token");
    const start = this.pos;
    this.pos += match[0].length;
    return { kind: "number", raw: match[0], start, end: this.pos };
  }

  private parseObject(depth: number): JsonNode {
    if (depth > this.maxDepth) throw new JsonSyntaxError("too deep");
    const start = this.pos;
    this.pos++;
    const entries = new Map<string, JsonNode>();
    this.skipWhitespace();
    if (this.text[this.pos] === "}") {
      this.pos++;
      return { kind: "object", entries, start, end: this.pos };
    }
    for (;;) {
      this.skipWhitespace();
      if (this.text[this.pos] !== '"') throw new JsonSyntaxError("expected member name");
      const name = this.parseString();
      if (entries.has(name)) throw new JsonSyntaxError("duplicate member name");
      this.skipWhitespace();
      if (this.text[this.pos] !== ":") throw new JsonSyntaxError("expected colon");
      this.pos++;
      this.skipWhitespace();
      entries.set(name, this.parseValue(depth));
      this.skipWhitespace();
      const next = this.text[this.pos];
      this.pos++;
      if (next === "}") return { kind: "object", entries, start, end: this.pos };
      if (next !== ",") throw new JsonSyntaxError("expected comma");
    }
  }

  private parseArray(depth: number): JsonNode {
    if (depth > this.maxDepth) throw new JsonSyntaxError("too deep");
    const start = this.pos;
    this.pos++;
    const items: JsonNode[] = [];
    this.skipWhitespace();
    if (this.text[this.pos] === "]") {
      this.pos++;
      return { kind: "array", items, start, end: this.pos };
    }
    for (;;) {
      this.skipWhitespace();
      items.push(this.parseValue(depth));
      this.skipWhitespace();
      const next = this.text[this.pos];
      this.pos++;
      if (next === "]") return { kind: "array", items, start, end: this.pos };
      if (next !== ",") throw new JsonSyntaxError("expected comma");
    }
  }

  private parseString(): string {
    const t = this.text;
    this.pos++;
    let out = "";
    let runStart = this.pos;
    while (this.pos < t.length) {
      const c = t.charCodeAt(this.pos);
      if (c === 0x22) {
        out += t.slice(runStart, this.pos);
        this.pos++;
        return out;
      }
      if (c < 0x20) throw new JsonSyntaxError("control character in string");
      if (c !== 0x5c) {
        this.pos++;
        continue;
      }
      out += t.slice(runStart, this.pos);
      const e = t[this.pos + 1];
      this.pos += 2;
      switch (e) {
        case '"':
          out += '"';
          break;
        case "\\":
          out += "\\";
          break;
        case "/":
          out += "/";
          break;
        case "b":
          out += "\b";
          break;
        case "f":
          out += "\f";
          break;
        case "n":
          out += "\n";
          break;
        case "r":
          out += "\r";
          break;
        case "t":
          out += "\t";
          break;
        case "u": {
          const hex = t.slice(this.pos, this.pos + 4);
          if (!/^[0-9a-fA-F]{4}$/.test(hex)) throw new JsonSyntaxError("bad unicode escape");
          out += String.fromCharCode(parseInt(hex, 16));
          this.pos += 4;
          break;
        }
        default:
          throw new JsonSyntaxError("bad escape");
      }
      runStart = this.pos;
    }
    throw new JsonSyntaxError("unterminated string");
  }
}

/** Parses `text` strictly. Throws {@link JsonSyntaxError} on any problem. */
export function parseStrict(text: string, maxDepth: number): JsonNode {
  return new Reader(text, maxDepth).parseDocument();
}

/** Converts a parsed node to plain JavaScript values. Numbers outside the double range are rejected. */
export function toPlain(node: JsonNode): unknown {
  switch (node.kind) {
    case "object": {
      const out: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
      for (const [k, v] of node.entries) out[k] = toPlain(v);
      return out;
    }
    case "array":
      return node.items.map(toPlain);
    case "number": {
      const n = Number(node.raw);
      if (!Number.isFinite(n)) throw new JsonSyntaxError("number out of range");
      return n;
    }
    case "string":
    case "boolean":
      return node.value;
    case "null":
      return null;
  }
}
