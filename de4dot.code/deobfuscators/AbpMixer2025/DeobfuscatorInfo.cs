/*
    Copyright (C) 2011-2015 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;

namespace de4dot.code.deobfuscators.AbpMixer2025 {
	public class DeobfuscatorInfo : DeobfuscatorInfoBase {
		public const string THE_NAME = "AbpMixer2025";
		public const string THE_TYPE = "abp";

		// Match fields like: m_d9490b70beca49a3934a96b01e6e0ead (hex suffix)
		// Case-insensitive, allow lengths commonly seen in obfuscators
		public DeobfuscatorInfo()
			: base("(?i)^m_[0-9a-f]{8,40}$") {
		}

		public override string Name => THE_NAME;
		public override string Type => THE_TYPE;

		public override IDeobfuscator CreateDeobfuscator() =>
			new Deobfuscator(new Deobfuscator.Options() {
				ValidNameRegex = new NameRegexes(".*"),
			});
	}
}
