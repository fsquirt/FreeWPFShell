


#[derive(Debug, Clone)]
#[allow(dead_code)] 
pub enum Json {
    Null,
    Bool(bool),
    Num(f64),
    Str(String),
    Arr(Vec<Json>),
    Obj(Vec<(String, Json)>),
}

const MAX_DEPTH: usize = 32;

impl Json {
    pub fn get(&self, key: &str) -> Option<&Json> {
        match self {
            Json::Obj(pairs) => pairs.iter().find(|(k, _)| k == key).map(|(_, v)| v),
            _ => None,
        }
    }

    pub fn as_str(&self) -> Option<&str> {
        match self { Json::Str(s) => Some(s), _ => None }
    }

    pub fn as_u64(&self) -> Option<u64> {
        match self {
            Json::Num(n) if *n >= 0.0 && n.fract() == 0.0 && *n <= u64::MAX as f64 => Some(*n as u64),
            _ => None,
        }
    }

    pub fn as_bool(&self) -> Option<bool> {
        match self { Json::Bool(b) => Some(*b), _ => None }
    }
}

pub fn parse(data: &[u8]) -> Result<Json, ()> {
    let mut p = Parser { data, pos: 0 };
    p.skip_ws();
    let v = p.parse_value(0)?;
    p.skip_ws();
    if p.pos != p.data.len() { return Err(()); }
    Ok(v)
}

struct Parser<'a> {
    data: &'a [u8],
    pos: usize,
}

impl<'a> Parser<'a> {
    fn peek(&self) -> Option<u8> {
        self.data.get(self.pos).copied()
    }

    fn skip_ws(&mut self) {
        while matches!(self.peek(), Some(b' ' | b'\t' | b'\n' | b'\r')) {
            self.pos += 1;
        }
    }

    fn expect(&mut self, b: u8) -> Result<(), ()> {
        if self.peek() == Some(b) { self.pos += 1; Ok(()) } else { Err(()) }
    }

    fn parse_value(&mut self, depth: usize) -> Result<Json, ()> {
        if depth > MAX_DEPTH { return Err(()); }
        match self.peek().ok_or(())? {
            b'{' => self.parse_object(depth),
            b'[' => self.parse_array(depth),
            b'"' => Ok(Json::Str(self.parse_string()?)),
            b't' => self.parse_literal("true", Json::Bool(true)),
            b'f' => self.parse_literal("false", Json::Bool(false)),
            b'n' => self.parse_literal("null", Json::Null),
            b'-' | b'0'..=b'9' => self.parse_number(),
            _ => Err(()),
        }
    }

    fn parse_literal(&mut self, lit: &str, val: Json) -> Result<Json, ()> {
        if self.data[self.pos..].starts_with(lit.as_bytes()) {
            self.pos += lit.len();
            Ok(val)
        } else {
            Err(())
        }
    }

    fn parse_object(&mut self, depth: usize) -> Result<Json, ()> {
        self.expect(b'{')?;
        let mut pairs = Vec::new();
        self.skip_ws();
        if self.peek() == Some(b'}') { self.pos += 1; return Ok(Json::Obj(pairs)); }
        loop {
            self.skip_ws();
            let key = self.parse_string()?;
            self.skip_ws();
            self.expect(b':')?;
            self.skip_ws();
            let val = self.parse_value(depth + 1)?;
            pairs.push((key, val));
            self.skip_ws();
            match self.peek().ok_or(())? {
                b',' => { self.pos += 1; }
                b'}' => { self.pos += 1; return Ok(Json::Obj(pairs)); }
                _ => return Err(()),
            }
        }
    }

    fn parse_array(&mut self, depth: usize) -> Result<Json, ()> {
        self.expect(b'[')?;
        let mut items = Vec::new();
        self.skip_ws();
        if self.peek() == Some(b']') { self.pos += 1; return Ok(Json::Arr(items)); }
        loop {
            self.skip_ws();
            items.push(self.parse_value(depth + 1)?);
            self.skip_ws();
            match self.peek().ok_or(())? {
                b',' => { self.pos += 1; }
                b']' => { self.pos += 1; return Ok(Json::Arr(items)); }
                _ => return Err(()),
            }
        }
    }

    fn parse_string(&mut self) -> Result<String, ()> {
        self.expect(b'"')?;
        let mut out = String::new();
        loop {
            let b = self.peek().ok_or(())?;
            match b {
                b'"' => { self.pos += 1; return Ok(out); }
                b'\\' => {
                    self.pos += 1;
                    let esc = self.peek().ok_or(())?;
                    self.pos += 1;
                    match esc {
                        b'"' => out.push('"'),
                        b'\\' => out.push('\\'),
                        b'/' => out.push('/'),
                        b'b' => out.push('\u{0008}'),
                        b'f' => out.push('\u{000C}'),
                        b'n' => out.push('\n'),
                        b'r' => out.push('\r'),
                        b't' => out.push('\t'),
                        b'u' => {
                            let hi = self.parse_hex4()?;
                            let cp = if (0xD800..0xDC00).contains(&hi) {
                                // 代理对：\uXXXX\uYYYY
                                if self.peek() == Some(b'\\') {
                                    self.pos += 1;
                                    if self.peek() != Some(b'u') { return Err(()); }
                                    self.pos += 1;
                                    let lo = self.parse_hex4()?;
                                    if !(0xDC00..0xE000).contains(&lo) { return Err(()); }
                                    0x10000 + ((hi - 0xD800) << 10) + (lo - 0xDC00)
                                } else {
                                    return Err(());
                                }
                            } else {
                                hi
                            };
                            out.push(char::from_u32(cp).ok_or(())?);
                        }
                        _ => return Err(()),
                    }
                }
                _ => {
                    // 直接按 UTF-8 取一个字符（含多字节）
                    let start = self.pos;
                    self.pos += 1;
                    while self.pos < self.data.len() && (self.data[self.pos] & 0xC0) == 0x80 {
                        self.pos += 1;
                    }
                    let s = std::str::from_utf8(&self.data[start..self.pos]).map_err(|_| ())?;
                    out.push_str(s);
                }
            }
        }
    }

    fn parse_hex4(&mut self) -> Result<u32, ()> {
        if self.pos + 4 > self.data.len() { return Err(()); }
        let hex = std::str::from_utf8(&self.data[self.pos..self.pos + 4]).map_err(|_| ())?;
        let v = u32::from_str_radix(hex, 16).map_err(|_| ())?;
        self.pos += 4;
        Ok(v)
    }

    fn parse_number(&mut self) -> Result<Json, ()> {
        let start = self.pos;
        while matches!(self.peek(), Some(b'-' | b'+' | b'0'..=b'9' | b'.' | b'e' | b'E')) {
            self.pos += 1;
        }
        let s = std::str::from_utf8(&self.data[start..self.pos]).map_err(|_| ())?;
        s.parse::<f64>().map(Json::Num).map_err(|_| ())
    }
}
