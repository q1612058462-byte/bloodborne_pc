// SPDX-License-Identifier: GPL-2.0-or-later
#include "common/serdes.h"

#include <cassert>
#include <string>
#include <vector>

template <typename F>
static void ExpectCorrupt(F&& read) {
    bool caught = false;
    try {
        read();
    } catch (const Serialization::CorruptArchive&) {
        caught = true;
    }
    assert(caught);
}

int main() {
    Serialization::Archive written;
    Serialization::Writer writer{written};
    writer.Write(u32{42});
    writer.Write(std::string{"face"});
    writer.Write(std::vector<u32>{1, 2, 3});

    auto bytes = written.TakeOff();
    Serialization::Archive valid{std::vector<u8>{bytes}};
    Serialization::Reader reader{valid};
    u32 value{};
    std::string name;
    std::vector<u32> numbers;
    reader.Read(value);
    reader.Read(name);
    reader.Read(numbers);
    assert(value == 42 && name == "face" && numbers == std::vector<u32>({1, 2, 3}));

    Serialization::Archive truncated{std::vector<u8>{42}};
    Serialization::Reader short_reader{truncated};
    ExpectCorrupt([&] { short_reader.Read(value); });

    Serialization::Archive oversized_string;
    Serialization::Writer string_writer{oversized_string};
    string_writer.Write(size_t{1000});
    Serialization::Reader string_reader{oversized_string};
    ExpectCorrupt([&] { string_reader.Read(name); });

    Serialization::Archive oversized_vector;
    Serialization::Writer vector_writer{oversized_vector};
    vector_writer.Write(size_t{1000});
    Serialization::Reader vector_reader{oversized_vector};
    ExpectCorrupt([&] { vector_reader.Read(numbers); });

    Serialization::Archive overrun{std::vector<u8>{1}};
    ExpectCorrupt([&] { overrun.Advance(2); });
}
