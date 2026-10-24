import numpy as np
import struct

from numpy.typing import NDArray


def write_padding(amount: int) -> bytes:
    if amount == 0:
        return b''
    
    return bytes([0] * amount)

class BinaryReader:
    def __init__(self, data: bytes):
        self.data   = data
        self.pos    = 0
        self.length = len(data)
    
    def read_struct(self, format_str: str):
        size = struct.calcsize(format_str)
        if self.pos + size > self.length:
            raise EOFError("End of stream")
        
        result = struct.unpack_from(format_str, self.data, self.pos)
        self.pos += size
        return result[0] if len(result) == 1 else result
    
    def read_byte(self, signed=False) -> int:
        sign = 'b' if signed else 'B'
        return self.read_struct(f'<{sign}')
    
    def read_bytes(self, length: int) -> bytes:
        if self.pos + length > self.length:
            raise EOFError("End of stream")
        result = self.data[self.pos:self.pos + length]
        self.pos += length
        return result
    
    def read_bool(self) -> bool:
        return self.read_struct('<?')
    
    def read_uint16(self) -> int:
        return self.read_struct('<H')
    
    def read_uint32(self) -> int:
        return self.read_struct('<I')

    def read_float(self) -> float:
        return self.read_struct('<f')
    
    def read_array(self, length: int, format_str: str='I', endian: str='<') -> list[int | float]:
        array = []
        for _ in range(length):
            array.append(self.read_struct(f'{endian}{format_str}'))
        
        return array
    
    def read_to_ndarray(self, dtype: str, count: int) -> NDArray:
        itemsize     = np.dtype(dtype).itemsize
        bytes_needed = itemsize * count
        if bytes_needed > self.remaining_bytes():
            raise ValueError(f"Not enough data to read {count} items of {dtype}")
    
        array = np.frombuffer(
                        self.data[self.pos: self.pos + bytes_needed],
                        dtype,
                        count,
                        0
                    ).copy()

        self.pos += np.dtype(dtype).itemsize * count
        return array

    def read_string(self, length: int, encoding: str='utf-8') -> str:
        raw_bytes = self.read_bytes(length)
    
        null_index   = raw_bytes.find(b'\x00')
        string_bytes = raw_bytes if null_index == -1 else raw_bytes[:null_index]

        try:
            return string_bytes.decode(encoding)
        except UnicodeDecodeError as e:
            raise UnicodeDecodeError(encoding, raw_bytes, self.pos, self.pos + length, f"Couldn't decode string: {e}")

    def remaining_bytes(self) -> int:
        return self.length - self.pos
